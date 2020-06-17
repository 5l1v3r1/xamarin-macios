/*
 * Copyright 2014 Xamarin Inc. All rights reserved.
 * Copyright 2019, 2020 Microsoft Corp. All rights reserved.
 *
 * Authors:
 *   Rolf Bjarne Kvinge <rolf@xamarin.com>
 *
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Xamarin.Utils {
	public class Execution {
		public string FileName;
		public IList<string> Arguments;
		public IDictionary<string, string> Environment;
		public string WorkingDirectory;
		public TimeSpan? Timeout;
		public CancellationToken? CancellationToken;

		public TextWriter Log;

		public int ExitCode { get; private set; }
		public bool TimedOut { get; private set; }
		public TextWriter StandardOutput { get; private set; }
		public TextWriter StandardError { get; private set; }

		static Thread StartOutputThread (TaskCompletionSource<Execution> tcs, object lockobj, StreamReader reader, TextWriter writer, string thread_name)
		{
			var thread = new Thread (() => {
				try {
					string line;
					while ((line = reader.ReadLine ()) != null) {
						lock (lockobj)
							writer.WriteLine (line);
					}
				} catch (Exception e) {
					tcs.TrySetException (e);
				} finally {
					// The Process instance doesn't dispose these streams, which means we need to do it,
					// otherwise we can run out of file descriptors while waiting for the GC to kick in.
					// Ref: https://bugzilla.xamarin.com/show_bug.cgi?id=43462
					reader.Dispose ();
				}
			}) {
				IsBackground = true,
				Name = thread_name,
			};
			thread.Start ();
			return thread;
		}

		public Task<Execution> RunAsync ()
		{
			var tcs = new TaskCompletionSource<Execution> ();
			var lockobj = new object ();

			try {
				var p = new Process ();
				p.StartInfo.FileName = FileName;
				p.StartInfo.Arguments = StringUtils.FormatArguments (Arguments);
				p.StartInfo.UseShellExecute = false;
				p.StartInfo.RedirectStandardInput = false;
				p.StartInfo.RedirectStandardOutput = true;
				p.StartInfo.RedirectStandardError = true;
				if (!string.IsNullOrEmpty (WorkingDirectory))
					p.StartInfo.WorkingDirectory = WorkingDirectory;

				// mtouch/mmp writes UTF8 data outside of the ASCII range, so we need to make sure
				// we read it in the same format. This also means we can't use the events to get
				// stdout/stderr, because mono's Process class parses those using Encoding.Default.
				p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
				p.StartInfo.StandardErrorEncoding = Encoding.UTF8;

				if (Environment != null) {
					foreach (var kvp in Environment) {
						if (kvp.Value == null) {
							p.StartInfo.EnvironmentVariables.Remove (kvp.Key);
						} else {
							p.StartInfo.EnvironmentVariables [kvp.Key] = kvp.Value;
						}
					}
				}

				StandardOutput ??= new StringWriter ();
				StandardError ??= new StringWriter ();

				if (Log != null) {
					if (!string.IsNullOrEmpty (p.StartInfo.WorkingDirectory))
						Log.Write ($"cd {StringUtils.Quote (p.StartInfo.WorkingDirectory)} && ");
					Log.WriteLine ("{0} {1}", p.StartInfo.FileName, p.StartInfo.Arguments);
				}
				p.Start ();
				var pid = p.Id;

				var stdoutThread = StartOutputThread (tcs, lockobj, p.StandardOutput, StandardOutput, $"StandardOutput reader for {p.StartInfo.FileName} (PID: {pid})");
				var stderrThread = StartOutputThread (tcs, lockobj, p.StandardError, StandardError, $"StandardError reader for {p.StartInfo.FileName} (PID: {pid})");

				CancellationToken?.Register (() => {
					// Don't call tcs.TrySetCanceled, that won't return an Execution result to the caller.
					try {
						p.Kill ();
					} catch (Exception ex) {
						// The process could be disposed already. Just ignore any exceptions here.
						Log?.WriteLine ($"Failed to cancel and kill PID {pid}: {ex.Message}");
					}
				});

				var thread = new Thread (() => {
					try {
						if (Timeout.HasValue) {
							if (!p.WaitForExit ((int) Timeout.Value.TotalMilliseconds)) {
								Log?.WriteLine ($"Command '{p.StartInfo.FileName} {p.StartInfo.Arguments}' didn't finish in {Timeout.Value.TotalMilliseconds} minutes, and will be killed.");
								TimedOut = true;
								try {
									p.Kill ();
								} catch (Exception ex) {
									// According to the documentation, there can be exceptions here we can't prepare for, so just ignore them.
									Log?.WriteLine ($"Failed to kill PID {pid}: {ex.Message}");
								}
							}
						}
						// Always call this WaitForExit overload to be make sure the stdout/stderr buffers have been flushed,
						// even if we've called the WaitForExit (int) overload
						p.WaitForExit ();
						ExitCode = p.ExitCode;

						stdoutThread.Join (TimeSpan.FromSeconds (1));
						stderrThread.Join (TimeSpan.FromSeconds (1));

						tcs.TrySetResult (this);
					} catch (Exception e) {
						tcs.TrySetException (e);
					} finally {
						p.Dispose ();
					}
				}) {
					IsBackground = true,
					Name = $"Thread waiting for {p.StartInfo.FileName} (PID: {pid}) to finish",
				};
				thread.Start ();
			} catch (Exception e) {
				tcs.TrySetException (e);
			}

			return tcs.Task;
		}

		public static Task<Execution> RunWithCallbacksAsync (string filename, IList<string> arguments, Dictionary<string, string> environment = null, Action<string> standardOutput = null, Action<string> standardError = null, TextWriter log = null, string workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
		{
			CallbackWriter outputCallback = null;
			CallbackWriter errorCallback = null;
			if (standardOutput != null)
				outputCallback = new CallbackWriter { Callback = standardOutput };
			if (standardOutput == standardError)
				errorCallback = outputCallback;
			else if (standardError != null)
				errorCallback = new CallbackWriter { Callback = standardError };
			return RunAsync (filename, arguments, environment, outputCallback, errorCallback, log, workingDirectory, timeout, cancellationToken);
		}

		public static Task<Execution> RunAsync (string filename, IList<string> arguments, Dictionary<string, string> environment = null, TextWriter standardOutput = null, TextWriter standardError = null, TextWriter log = null, string workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
		{
			return new Execution {
				FileName = filename,
				Arguments = arguments,
				Environment = environment,
				StandardOutput = standardOutput,
				StandardError = standardError,
				WorkingDirectory = workingDirectory,
				CancellationToken = cancellationToken,
				Timeout = timeout,
			}.RunAsync ();
		}

		public static Task<Execution> RunAsync (string filename, IList<string> arguments, Dictionary<string, string> environment = null, bool mergeOutput = false, string workingDirectory = null, TextWriter log = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
		{
			var standardOutput = new StringWriter ();
			var standardError = mergeOutput ? standardOutput : new StringWriter ();
			return RunAsync (filename, arguments, environment, standardOutput, standardError, log, workingDirectory, timeout, cancellationToken);
		}

		public static Task<Execution> RunWithStringBuildersAsync (string filename, IList<string> arguments, Dictionary<string, string> environment = null, StringBuilder standardOutput = null, StringBuilder standardError = null, TextWriter log = null, string workingDirectory = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
		{
			var stdout = standardOutput == null ? null : new StringWriter (standardOutput);
			var stderr = standardError == null ? null : (standardOutput == standardError ? stdout : new StringWriter (standardError));
			return RunAsync (filename, arguments, environment, stdout, stderr, log, workingDirectory, timeout, cancellationToken);
		}

		class CallbackWriter : TextWriter {
			public Action<string> Callback;
			public override void WriteLine (string value)
			{
				Callback (value);
			}

			public override Encoding Encoding => Encoding.UTF8;
		}
	}
}
