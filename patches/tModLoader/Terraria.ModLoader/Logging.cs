﻿using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Terraria.Localization;
using Terraria.ModLoader.Core;
using Microsoft.Xna.Framework;

namespace Terraria.ModLoader
{
	public static class Logging
	{
		public static readonly string LogDir = Path.Combine(Program.SavePath, "Logs");
		public static string LogPath { get; private set; }

		internal static ILog Terraria { get; } = LogManager.GetLogger("Terraria");
		internal static ILog tML { get; } = LogManager.GetLogger("tML");

#if CLIENT
		internal const string side = "client";
#else
		internal const string side = "server";
#endif

		internal static void Init() {
			if (Program.LaunchParameters.ContainsKey("-build"))
				return;

			if (!Directory.Exists(LogDir))
				Directory.CreateDirectory(LogDir);

			ConfigureAppenders();

			tML.InfoFormat("Starting {0} {1} {2}", ModLoader.versionedName, ReLogic.OS.Platform.Current.Type, side);
			tML.InfoFormat("Running on {0} {1}", FrameworkVersion.Framework, FrameworkVersion.Version);
			tML.InfoFormat("Executable: {0}", Assembly.GetEntryAssembly().Location);
			tML.InfoFormat("Working Directory: {0}", Path.GetFullPath(Directory.GetCurrentDirectory()));
			tML.InfoFormat("Launch Parameters: {0}", string.Join(" ", Program.LaunchParameters.Select(p => (p.Key + " " + p.Value).Trim())));

			if (ModCompile.DeveloperMode)
				tML.Info("Developer mode enabled");

			AppDomain.CurrentDomain.UnhandledException += (s, args) => tML.Error("Unhandled Exception", args.ExceptionObject as Exception);
			LogFirstChanceExceptions();
			EnablePortablePDBTraces();
			AssemblyResolving.Init();
			LoggingHooks.Init();
			LogArchiver.ArchiveLogs();
		}

		private static void ConfigureAppenders() {
			var layout = new PatternLayout {
				ConversionPattern = "[%d{HH:mm:ss}] [%t/%level] [%logger]: %m%n"
			};
			layout.ActivateOptions();

			var appenders = new List<IAppender>();
#if CLIENT
			appenders.Add(new ConsoleAppender {
				Name = "ConsoleAppender",
				Layout = layout
			});
#else
			appenders.Add(new DebugAppender {
				Name = "DebugAppender",
				Layout = layout
			});
#endif

			var fileAppender = new FileAppender {
				Name = "FileAppender",
				File = LogPath = Path.Combine(LogDir, GetNewLogFile(side)),
				AppendToFile = false,
				Encoding = Encoding.UTF8,
				Layout = layout
			};
			fileAppender.ActivateOptions();
			appenders.Add(fileAppender);

			BasicConfigurator.Configure(appenders.ToArray());
		}

		private static string GetNewLogFile(string baseName) {
			var pattern = new Regex($"{baseName}(\\d*)\\.log");
			var existingLogs = Directory.GetFiles(LogDir).Where(s => pattern.IsMatch(Path.GetFileName(s))).ToList();

			if (!existingLogs.All(CanOpen)) {
				int n = existingLogs.Select(s => {
					var tok = pattern.Match(Path.GetFileName(s)).Groups[1].Value;
					return tok.Length == 0 ? 1 : int.Parse(tok);
				}).Max();
				return $"{baseName}{n + 1}.log";
			}

			foreach (var existingLog in existingLogs.OrderBy(File.GetCreationTime))
				File.Move(existingLog, existingLog + ".old");

			return $"{baseName}.log";
		}

		private static bool CanOpen(string fileName) {
			try {
				using (new FileStream(fileName, FileMode.Append)) ;
				return true;
			}
			catch (IOException) {
				return false;
			}
		}

		private static void LogFirstChanceExceptions() {
			if (FrameworkVersion.Framework == Framework.Mono)
				tML.Warn("First-chance exception reporting is not implemented on Mono");

			AppDomain.CurrentDomain.FirstChanceException += FirstChanceExceptionHandler;
		}

		private static HashSet<string> pastExceptions = new HashSet<string>();
		internal static void ResetPastExceptions() => pastExceptions.Clear();

		private static HashSet<string> ignoreSources = new HashSet<string> {
			"MP3Sharp"
		};
		public static void IgnoreExceptionSource(string source) => ignoreSources.Add(source);

		private static List<string> ignoreContents = new List<string> {
			"System.Console.set_OutputEncoding", // when the game is launched without a console handle (client outside dev environment)
			"Terraria.ModLoader.ModCompile",
			"Delegate.CreateDelegateNoSecurityCheck",
			"MethodBase.GetMethodBody",
			"Terraria.Net.Sockets.TcpSocket.Terraria.Net.Sockets.ISocket.AsyncSend", // client disconnects from server
			"System.Diagnostics.Process.Kill", // attempt to kill non-started process when joining server
			"Terraria.ModLoader.Core.AssemblyManager.CecilAssemblyResolver.Resolve",
		};

		// there are a couple of annoying messages that happen during cancellation of asynchronous downloads
		// that have no other useful info to suppress by
		private static List<string> ignoreMessages = new List<string> {
			"A blocking operation was interrupted by a call to WSACancelBlockingCall", // c#.net abort for downloads
			"The request was aborted: The request was canceled.", // System.Net.ConnectStream.IOError
			"Object name: 'System.Net.Sockets.Socket'.", // System.Net.Sockets.Socket.BeginReceive
			"Object name: 'System.Net.Sockets.NetworkStream'",// System.Net.Sockets.NetworkStream.UnsafeBeginWrite
			"This operation cannot be performed on a completed asynchronous result object.", // System.Net.ContextAwareResult.get_ContextCopy()
			"Object name: 'SslStream'.", // System.Net.Security.SslState.InternalEndProcessAuthentication
			"Unable to load DLL 'Microsoft.DiaSymReader.Native.x86.dll'" // Roslyn
		};

		private static List<string> ignoreThrowingMethods = new List<string> {
			"at Terraria.Lighting.doColors_Mode", // vanilla lighting which bug randomly happens
			"System.Threading.CancellationToken.Throw", // an operation (task) was deliberately cancelled
		};

		public static void IgnoreExceptionContents(string source) {
			if (!ignoreContents.Contains(source))
				ignoreContents.Add(source);
		}

		private static Exception previousException;
		private static void FirstChanceExceptionHandler(object sender, FirstChanceExceptionEventArgs args) {
			if (args.Exception == previousException ||
				args.Exception is ThreadAbortException ||
				ignoreSources.Contains(args.Exception.Source) ||
				ignoreMessages.Any(str => args.Exception.Message?.Contains(str) ?? false) ||
				ignoreThrowingMethods.Any(str => args.Exception.StackTrace?.Contains(str) ?? false))
				return;

			var stackTrace = new StackTrace(true);
			PrettifyStackTraceSources(stackTrace.GetFrames());
			var traceString = stackTrace.ToString();

			if (ignoreContents.Any(traceString.Contains))
				return;

			traceString = traceString.Substring(traceString.IndexOf('\n'));
			var exString = args.Exception.GetType() + ": " + args.Exception.Message + traceString;
			lock (pastExceptions) {
				if (!pastExceptions.Add(exString))
					return;
			}

			previousException = args.Exception;
			var msg = args.Exception.Message + " " + Language.GetTextValue("tModLoader.RuntimeErrorSeeLogsForFullTrace", Path.GetFileName(LogPath));
#if CLIENT
			if (ModCompile.activelyModding)
				AddChatMessage(msg, Color.OrangeRed);
#else
			Console.ForegroundColor = ConsoleColor.DarkMagenta;
			Console.WriteLine(msg);
			Console.ResetColor();
#endif
			tML.Warn(Language.GetTextValue("tModLoader.RuntimeErrorSilentlyCaughtException") + '\n' + exString);
		}

		// Separate method to avoid triggering Main constructor
		private static void AddChatMessage(string msg, Color color) {
			if (Main.gameMenu)
				return;

			float soundVolume = Main.soundVolume;
			Main.soundVolume = 0f;
			Main.NewText(msg, color);
			Main.soundVolume = soundVolume;
		}

		private static Regex statusRegex = new Regex(@"(.+?)[: \d]*%$");
		internal static void LogStatusChange(string oldStatusText, string newStatusText) {
			// trim numbers and percentage to reduce log spam
			var oldBase = statusRegex.Match(oldStatusText).Groups[1].Value;
			var newBase = statusRegex.Match(newStatusText).Groups[1].Value;
			if (newBase != oldBase && newBase.Length > 0)
				LogManager.GetLogger("StatusText").Info(newBase);
		}

		internal static void ServerConsoleLine(string msg) => ServerConsoleLine(msg, Level.Info);
		internal static void ServerConsoleLine(string msg, Level level, Exception ex = null, ILog log = null) {
			if (level == Level.Warn)
				Console.ForegroundColor = ConsoleColor.Yellow;
			else if (level == Level.Error)
				Console.ForegroundColor = ConsoleColor.Red;

			Console.WriteLine(msg);
			Console.ResetColor();

			(log ?? Terraria).Logger.Log(null, level, msg, ex);
		}

		internal static readonly FieldInfo f_fileName =
			typeof(StackFrame).GetField("strFileName", BindingFlags.Instance | BindingFlags.NonPublic) ??
			typeof(StackFrame).GetField("fileName", BindingFlags.Instance | BindingFlags.NonPublic);

		private static readonly Assembly TerrariaAssembly = Assembly.GetExecutingAssembly();

		public static void PrettifyStackTraceSources(StackFrame[] frames) {
			if (frames == null)
				return;

			foreach (var frame in frames) {
				string filename = frame.GetFileName();
				var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
				if (filename == null || assembly == null)
					continue;

				string trim;
				if (AssemblyManager.GetAssemblyOwner(assembly, out var modName))
					trim = modName;
				else if (assembly == TerrariaAssembly)
					trim = "tModLoader";
				else
					continue;

				int idx = filename.LastIndexOf(trim, StringComparison.InvariantCultureIgnoreCase);
				if (idx > 0) {
					filename = filename.Substring(idx);
					f_fileName.SetValue(frame, filename);
				}
			}
		}

		private static void EnablePortablePDBTraces() {
			if (FrameworkVersion.Framework == Framework.NetFramework && FrameworkVersion.Version >= new Version(4, 7, 2))
				Type.GetType("System.AppContextSwitches").GetField("_ignorePortablePDBsInStackTraces", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, -1);
		}
	}
}
