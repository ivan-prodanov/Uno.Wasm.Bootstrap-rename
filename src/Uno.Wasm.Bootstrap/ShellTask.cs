// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
// 
// This file is based on the work from https://github.com/praeclarum/Ooui
// 
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Transactions;
using Microsoft.Build.Framework;
using Microsoft.Win32.SafeHandles;
using Mono.Cecil;
using Mono.CompilerServices.SymbolWriter;
using Newtonsoft.Json.Linq;
using Uno.Wasm.Bootstrap.Extensions;

namespace Uno.Wasm.Bootstrap
{
	public partial class ShellTask_v0 : Microsoft.Build.Utilities.Task
	{
		private const string WasmScriptsFolder = "WasmScripts";
		private const string ServiceWorkerFileName = "service-worker.js";
		private const string DeployMetadataName = "UnoDeploy";
		private static readonly char OtherDirectorySeparatorChar = Path.DirectorySeparatorChar == '/' ? '\\' : '/';
		private static readonly string _wwwwroot = "wwwroot" + Path.DirectorySeparatorChar;

		private string _distPath = "";
		private string _workDistPath = "";
		private string _workDistRootPath = "";
		private string _managedPath = "";
		private string _bclPath = "";
		private string[]? _bitcodeFilesCache;
		private List<string> _referencedAssemblies = new List<string>();
		private Dictionary<string, string>? _bclAssemblies;
		private readonly List<string> _dependencies = new List<string>();
		private string[]? _additionalStyles;
		private List<AssemblyDefinition>? _resourceSearchList;
		private RuntimeExecutionMode _runtimeExecutionMode;
		private ShellMode _shellMode;
		private string _linkerBinPath = "";
		private string _finalPackagePath = "";
		private string _remoteBasePackagePath = "";
		private string[]? _contentExtensionsToExclude;

		[Microsoft.Build.Framework.Required]
		public string CurrentProjectPath { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public string BuildTaskBasePath { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public string Assembly { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public string DistPath { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public string IntermediateOutputPath { get; set; } = "";
		[Microsoft.Build.Framework.Required]
		public string BaseIntermediateOutputPath { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public string MonoWasmSDKPath { get; set; } = "";

		public string? PackagerBinPath { get; set; }

		public bool UseFileIntegrity { get; set; } = true;

		/// <remarks>
		/// Disabled until browsers get smarted about fetch vs. prefetch invocations.
		/// e.g. Chrome downloads files twice.
		/// </remarks>
		public bool GeneratePrefetchHeaders { get; set; } = false;

		public Microsoft.Build.Framework.ITaskItem[]? ReferencePath { get; set; }

		public Microsoft.Build.Framework.ITaskItem[]? MonoEnvironment { get; set; }

		[Microsoft.Build.Framework.Required]
		public string TargetFrameworkIdentifier { get; set; } = "";

		public string TargetFramework { get; set; } = "";

		public string TargetFrameworkVersion { get; set; } = "0.0";

		[Microsoft.Build.Framework.Required]
		public string IndexHtmlPath { get; set; } = "";

		[Required]
		public string WasmShellMode { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public string MonoRuntimeExecutionMode { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public bool MonoILLinker { get; set; }

		/// <summary>
		/// Path override for the mono-wasm SDK folder
		/// </summary>
		public string? MonoTempFolder { get; private set; }

		public string AssembliesFileExtension { get; set; } = "clr";

		public Microsoft.Build.Framework.ITaskItem[]? Assets { get; set; }

		public string? ContentExtensionsToExclude { get; set; }

		public Microsoft.Build.Framework.ITaskItem[]? AotProfile { get; set; }

		public Microsoft.Build.Framework.ITaskItem[]? LinkerDescriptors { get; set; }

		public Microsoft.Build.Framework.ITaskItem[]? MixedModeExcludedAssembly { get; set; }

		public Microsoft.Build.Framework.ITaskItem[]? CompressedExtensions { get; set; }

		public Microsoft.Build.Framework.ITaskItem[]? ExtraEmccFlags { get; set; }

		public bool GenerateCompressedFiles { get; set; }

		public bool ForceUseWSL { get; set; }

		public bool ForceDisableWSL { get; set; }

		[Microsoft.Build.Framework.Required]
		public string RuntimeConfiguration { get; set; } = "";

		[Microsoft.Build.Framework.Required]
		public bool RuntimeDebuggerEnabled { get; set; }

		public int BrotliCompressionQuality { get; set; } = 7;

		public string? CustomDebuggerPath { get; set; }

		public string? CustomLinkerPath { get; set; }

		public string? PWAManifestFile { get; set; }

		public bool EnableLongPathSupport { get; set; } = true;

		public bool EnableEmccProfiling { get; set; } = false;

		public bool EnableNetCoreICU { get; set; } = false;

		public bool GenerateAOTProfile { get; set; } = false;

		[Output]
		public string? OutputPackagePath { get; private set; }

		[Output]
		public string? OutputDistPath { get; private set; }

		private Version ActualTargetFrameworkVersion => Version.TryParse(TargetFrameworkVersion.Substring(1), out var v) ? v : new Version("0.0");

		public override bool Execute()
		{
			try
			{
				//if (TargetFrameworkIdentifier != ".NETStandard" && TargetFrameworkIdentifier != ".NETCoreApp")
				//{
				//	Log.LogWarning($"The package Uno.Wasm.Bootstrap is not supported for the current project ({Assembly}), skipping dist generation.");
				//	return true;
				//}

				if (string.IsNullOrEmpty(TargetFramework))
				{
					throw new InvalidOperationException($"The TargetFramework task parameter must be defined.");
				}

				if(TargetFrameworkIdentifier == ".NETCoreApp" && ActualTargetFrameworkVersion < new Version("5.0"))
				{
					throw new InvalidOperationException($"The TargetFramework version must be above 5.0 (found {TargetFrameworkVersion})");
				}

				// Debugger.Launch();

				PreloadAssemblies();
				TryEnableLongPathAware();
				ParseProperties();
				GetBcl();
				CreateDist();
				CopyContent();
				CopyRuntime();
				RunPackager();
				TryDeployDebuggerProxy();
				ExtractAdditionalJS();
				ExtractAdditionalCSS();
				CleanupDist();
				PrepareFinalDist();
				GenerateConfig();
				TouchServiceWorker();
				MergeConfig();
				GenerateHtml();
				TryCompressDist();

				return true;
			}
			catch (Exception ex)
			{
				Log.LogError(ex.ToString(), false, true, null);
				return false;
			}
		}

		private bool IsNetCoreWasm =>
			TargetFrameworkIdentifier == ".NETCoreApp" && ActualTargetFrameworkVersion >= new Version("5.0");

		private void PreloadAssemblies()
		{
			// Under some circumstances, the assemblies bundled with the bootstrapper do not
			// get loaded properly by .NET Core. This is method forces the loading of those
			// assemblies in order to let the loading find them automatically.

			var path = Path.GetDirectoryName(new Uri(GetType().Assembly.GetName().CodeBase).LocalPath);

			foreach (var file in Directory.GetFiles(path, "*.dll"))
			{
				_ = System.Reflection.Assembly.LoadFile(Path.Combine(path, file));
			}
		}

		private void TryEnableLongPathAware()
		{
			if (EnableLongPathSupport)
			{
				// In some cases, particularly when using the Azure publish task
				// long paths using the "\\?\" prefix is not supported. Fallback on
				// standard paths in such cases.

				try
				{
					var path = TryConvertLongPath(Path.GetFullPath(DistPath));

					DirectoryCreateDirectory(path);
				}
				catch (ArgumentException e)
				{
					Log.LogMessage($"Long path format use failed, falling back to standard path (Error: {e.Message})");
					EnableLongPathSupport = false;
				}
			}

			IntermediateOutputPath = TryConvertLongPath(IntermediateOutputPath);
			BaseIntermediateOutputPath = TryConvertLongPath(Path.GetFullPath(BaseIntermediateOutputPath));
			DistPath = TryConvertLongPath(DistPath);
			CurrentProjectPath = TryConvertLongPath(CurrentProjectPath);
			MonoTempFolder = TryConvertLongPath(MonoTempFolder!);
			CustomDebuggerPath = TryConvertLongPath(CustomDebuggerPath!);
		}


		private void TouchServiceWorker()
		{
			// The service worker file must change to be reloaded properly, add the dist digest
			// as cache trasher.
			var workerFilePath = Path.Combine(_finalPackagePath, ServiceWorkerFileName);
			var workerBody = File.ReadAllText(workerFilePath);

			workerBody = workerBody.Replace("$(CACHE_KEY)", Path.GetFileName(_remoteBasePackagePath));
			workerBody = workerBody.Replace("$(REMOTE_BASE_PATH)", _remoteBasePackagePath + "/");
			workerBody += $"\r\n\r\n// {Path.GetFileName(_managedPath)}";

			File.WriteAllText(workerFilePath, workerBody);

			// The service worker file must be placed at the root in order to specify the
			// "./" scope to include index.html in the worker. Otherwise, offline mode cannot
			// work if index.html is not cached.
			MoveFileSafe(workerFilePath, Path.Combine(_distPath, ServiceWorkerFileName));
		}

		/// <summary>
		/// Align paths to fix issues with mixed path
		/// </summary>
		string FixupPath(string path)
			=> path.Replace(OtherDirectorySeparatorChar, Path.DirectorySeparatorChar);

		private void FileCopy(string source, string dest, bool overwrite = false)
		{
			var sourceFileName = FixupPath(source);
			var destFileName = FixupPath(dest);

			try
			{
				File.Copy(sourceFileName, destFileName, overwrite);
			}
			catch (Exception ex)
			{
				Log.LogError($"Failed to copy {sourceFileName} to {destFileName}: {ex.Message}");
				throw;
			}
		}

		private void DirectoryCreateDirectory(string directory)
		{
			var directoryName = FixupPath(directory);

			try
			{
				Directory.CreateDirectory(directoryName);
			}
			catch (Exception /*e*/)
			{
				Log.LogError($"Failed to create directory [{directoryName}][{directory}]");
				throw;
			}
		}

		private void CleanupDist()
		{
			var unusedFiles = new[] {
				"*.wast",
				"*.bc",
				"*.a",
			};

			foreach (var unusedFile in unusedFiles)
			{
				foreach (var file in Directory.EnumerateFiles(_workDistPath, unusedFile))
				{
					Log.LogMessage(MessageImportance.Low, $"Removing unused file {file}");
					File.Delete(file);
				}
			}
		}

		private void TryCompressDist()
		{
			var hasCompressedExtensions = (CompressedExtensions?.Any() ?? false);

			if (
				!RuntimeDebuggerEnabled
				&& GenerateCompressedFiles
				&& hasCompressedExtensions
			)
			{
				var compressibleExtensions = CompressedExtensions
					.Select(e => e.ItemSpec);

				Log.LogMessage(MessageImportance.Low, $"Compressing {string.Join(", ", compressibleExtensions)}");

				var filesToCompress = compressibleExtensions
					.SelectMany(e => Directory.GetFiles(_finalPackagePath, "*" + e, SearchOption.AllDirectories))
					.Where(f => !Path.GetDirectoryName(f).Contains("_compressed_"))
					.Distinct()
					.ToArray();

				CompressFiles(filesToCompress, "gz", GzipCompress);
				CompressFiles(filesToCompress, "br", BrotliCompress);
			}
			else
			{
				Log.LogMessage(MessageImportance.Low,
					$"Compression is disabled (RuntimeDebuggerEnabled:{RuntimeDebuggerEnabled}, " +
					$"GenerateCompressedFiles:{GenerateCompressedFiles}, " +
					$"hasCompressedExtensions:{hasCompressedExtensions})");
			}
		}

		private void CompressFiles(string[] filesToCompress, string method, Action<string, string> compressHandler)
			=> filesToCompress
				.AsParallel()
				.Select(fileName =>
				{
					var compressedPathBase = Path.Combine(_distPath, "_compressed_" + method);

					var compressedFileName = fileName;
					compressedFileName = compressedFileName.Replace(_distPath, compressedPathBase);

					DirectoryCreateDirectory(Path.GetDirectoryName(compressedFileName));

					if (File.Exists(compressedFileName))
					{
						if (File.GetCreationTime(compressedFileName) < File.GetCreationTime(fileName))
						{
							Log.LogMessage(MessageImportance.Low, $"Deleting {compressedFileName} as the source has changed");
							File.Delete(compressedFileName);
						}
					}

					Log.LogMessage($"Compressing {fileName}->{compressedFileName}");

					compressHandler(fileName, compressedFileName);

					return true;
				})
				.ToArray();

		private void GzipCompress(string source, string destination)
		{
			using var inStream = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var compressedFileStream = File.Create(destination);
			using var compressionStream = new GZipStream(compressedFileStream, CompressionLevel.Optimal);

			inStream.CopyTo(compressionStream);
		}

		private void BrotliCompress(string source, string destination)
		{
			using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var output = File.Create(destination);
			using var bs = new BrotliSharpLib.BrotliStream(output, CompressionMode.Compress);

			// By default, BrotliSharpLib uses a quality value of 1 and window size of 22 if the methods are not called.
			bs.SetQuality(BrotliCompressionQuality);
			/** bs.SetWindow(windowSize); **/
			/** bs.SetCustomDictionary(customDict); **/
			input.CopyTo(bs);

			/* IMPORTANT: Only use the destination stream after closing/disposing the BrotliStream
			   as the BrotliStream must be closed in order to signal that no more blocks are being written
			   for the final block to be flushed out 
			*/
			bs.Dispose();
		}

		private bool IsWSLRequired =>
			Environment.OSVersion.Platform == PlatformID.Win32NT
			&& (GetBitcodeFilesParams().Any() || _runtimeExecutionMode != RuntimeExecutionMode.Interpreter || ForceUseWSL || GenerateAOTProfile || HasAotProfile);

		private bool HasAotProfile => AotProfile?.Any() ?? false;

		private (int exitCode, string output, string error) RunProcess(string executable, string parameters, string? workingDirectory = null)
		{
			if (IsWSLRequired && !ForceDisableWSL)
			{
				var unixPath = AlignPath(executable, escape: true);
				var dotnetRuntimePath = Path.GetExtension(executable).ToLowerInvariant()
					switch {
						".exe" => "mono",
						".dll" => "dotnet",
						_ => ""
					};
				var cwd = workingDirectory != null ? $"cd \\\"{AlignPath(workingDirectory, escape: true)}\\\" && " : "";

				parameters = $"-c \" {cwd} {dotnetRuntimePath} {unixPath} " + parameters.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
				executable = Path.Combine(Environment.GetEnvironmentVariable("WINDIR"), "sysnative", "bash.exe");

				if (!File.Exists(executable))
				{
					throw new InvalidOperationException(
						$"WSL is required for this build but could not be found (Searched for [{executable}]). " +
						$"WSL use may be explicitly disabled for CI Windows builds, see more details here: https://github.com/unoplatform/Uno.Wasm.Bootstrap#special-considerations-for-ci-servers-github-actions-azure-devops");
				}
			}
			else if (RuntimeHelpers.IsNetCore
				&& (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
				|| RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
				)
			)
			{
				if (executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
				{
					parameters = $"{executable} {parameters}";
					executable = "mono";
				}

				if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				{
					parameters = $"{executable} {parameters}";
					executable = "dotnet";
				}
			}
			else
			{
				if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				{
					parameters = $"{executable} {parameters}";
					executable = "dotnet";
				}
			}

			var p = new Process
			{
				StartInfo =
				{
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					FileName = executable,
					Arguments = parameters
				}
			};

			if (workingDirectory != null)
			{
				p.StartInfo.WorkingDirectory = workingDirectory;
			}

			Log.LogMessage($"Running [{p.StartInfo.WorkingDirectory}]: {p.StartInfo.FileName} {p.StartInfo.Arguments}");

			var output = new StringBuilder();
			var error = new StringBuilder();
			var elapsed = Stopwatch.StartNew();
			p.OutputDataReceived += (s, e) => { if (e.Data != null) { Log.LogMessage($"[{elapsed.Elapsed}] {e.Data}"); output.Append(e.Data); } };
			p.ErrorDataReceived += (s, e) => { if (e.Data != null) { Log.LogError($"[{elapsed.Elapsed}] {e.Data}"); error.Append(e.Data); } };

			if (p.Start())
			{
				p.BeginOutputReadLine();
				p.BeginErrorReadLine();
				p.WaitForExit();
				var exitCore = p.ExitCode;
				p.Close();

				return (exitCore, output.ToString(), error.ToString());
			}
			else
			{
				throw new Exception($"Failed to start [{executable}]");
			}
		}

		private string AlignPath(string path, bool escape = false)
		{
			var output = IsWSLRequired && !ForceDisableWSL ? $"`wslpath \"{path.Replace("\\\\?\\", "")}\"`" : path;

			if (escape)
			{
				output = output.Replace("\"", "\\\"");
			}

			return output;
		}

		private void TryDeployDebuggerProxy()
		{
			if (RuntimeDebuggerEnabled)
			{
				var sdkName = Path.GetFileName(MonoWasmSDKPath);

				var wasmDebuggerRootPath = Path.Combine(IntermediateOutputPath, "wasm-debugger");
				DirectoryCreateDirectory(wasmDebuggerRootPath);

				if (!string.IsNullOrEmpty(CustomDebuggerPath))
				{
					CustomDebuggerPath = FixupPath(CustomDebuggerPath!);
				}

				var debuggerLocalPath = Path.Combine(wasmDebuggerRootPath, sdkName);

				Log.LogMessage($"Debugger CustomDebuggerPath:[{CustomDebuggerPath}], {wasmDebuggerRootPath}, {debuggerLocalPath}, {sdkName}");

				if (!Directory.Exists(debuggerLocalPath))
				{
					foreach (var debugger in Directory.GetDirectories(wasmDebuggerRootPath))
					{
						Directory.Delete(debugger, recursive: true);
					}

					DirectoryCreateDirectory(debuggerLocalPath);

					var proxyBasePath = IsNetCoreWasm
						? Path.Combine(MonoWasmSDKPath, "dbg-proxy", "net5", "Release")
						: Path.Combine(MonoWasmSDKPath, "dbg-proxy", "netcoreapp3.0");

					var sourceBasePath = FixupPath(string.IsNullOrEmpty(CustomDebuggerPath) ? proxyBasePath : CustomDebuggerPath!);

					foreach (var debuggerFilePath in Directory.EnumerateFiles(sourceBasePath))
					{
						var debuggerFile = Path.GetFileName(debuggerFilePath);

						string sourceFileName = Path.Combine(sourceBasePath, debuggerFile);
						string destFileName = Path.Combine(debuggerLocalPath, debuggerFile);

						if (File.Exists(sourceFileName))
						{
							Log.LogMessage(MessageImportance.High, $"Copying {sourceFileName} -> {destFileName}");
							FileCopy(sourceFileName, destFileName);
						}
						else
						{
							Log.LogMessage($"Skipping [{sourceFileName}] as it does not exist");
						}
					}
				}
			}
		}

		private void RunPackager()
		{
			BuildReferencedAssembliesList();

			var workAotPath = Path.Combine(IntermediateOutputPath, "workAot");

			if (Directory.Exists(workAotPath))
			{
				Directory.Delete(workAotPath, true);
			}

			DirectoryCreateDirectory(workAotPath);

			var referencePathsParameter = string.Join(" ", _referencedAssemblies.Select(Path.GetDirectoryName).Distinct().Select(r => $"--search-path=\"{AlignPath(r)}\""));

			// Timezone support
			var releaseTimeZoneData = Path.Combine(BuildTaskBasePath, "..", "tools", "support", "Uno.Wasm.TimezoneData.dll");

			if (!IsNetCoreWasm)
			{
				referencePathsParameter += $" \"{AlignPath(releaseTimeZoneData)}\"";
			}

			if (GenerateAOTProfile)
			{
				var profilerSupport = Path.Combine(BuildTaskBasePath, "..", "tools", "support", "Uno.Wasm.Profiler.dll");
				referencePathsParameter += $" \"{AlignPath(profilerSupport)}\"";
			}

			var debugOption = RuntimeDebuggerEnabled ? "--debug" : "";
			string packagerBinPath = string.IsNullOrWhiteSpace(PackagerBinPath) ? Path.Combine(MonoWasmSDKPath, "packager.exe") : PackagerBinPath!;
			var appDirParm = $"--appdir=\"{AlignPath(_workDistPath)}\" ";

			// Determines if the packager needs to be used.
			var useFullPackager = !ForceDisableWSL && (IsRuntimeAOT() || GetBitcodeFilesParams().Any() || IsWSLRequired);

			var emsdkPath = useFullPackager ? ValidateEmscripten() : "";

			var enableICUParam = EnableNetCoreICU ? "--icu" : "";
			var monovmparams = IsNetCoreWasm ? $"--framework=net5 --runtimepack-dir={AlignPath(MonoWasmSDKPath)} {enableICUParam} " : "";

			//
			// Run the packager to create the original layout. The AOT will optionally run over this pass.
			//
			var packagerResults = RunProcess(packagerBinPath, $"--runtime-config={RuntimeConfiguration} {appDirParm} {monovmparams} --zlib {debugOption} {referencePathsParameter} \"{AlignPath(TryConvertLongPath(Path.GetFullPath(Assembly)))}\"", _workDistPath);

			if (packagerResults.exitCode != 0)
			{
				throw new Exception("Failed to generate wasm layout (More details are available in diagnostics mode or using the MSBuild /bl switch)");
			}

			if (useFullPackager)
			{
				var extraEmccFlags = (ExtraEmccFlags?.Select(f => f.ItemSpec) ?? new string[0]).ToList();
				var packagerParams = new List<string>();

				var mixedModeExcluded = MixedModeExcludedAssembly
					?.Select(a => a.ItemSpec)
					.ToArray() ?? Array.Empty<string>();

				var hasAotProfile = !GenerateAOTProfile && HasAotProfile;

				var mixedModeAotAssembliesParam = mixedModeExcluded.Any() && !hasAotProfile ? "--skip-aot-assemblies=" + string.Join(",", mixedModeExcluded) : "";
				var aotProfile = hasAotProfile ? $"\"--aot-profile={AlignPath(AotProfile.First().GetMetadata("FullPath"))}\"" : "";

				TryAdjustAOTProfile();

				var dynamicLibraries = GetDynamicLibrariesParams();
				var dynamicLibraryParams = dynamicLibraries.Any() ? "--pinvoke-libs=" + string.Join(",", dynamicLibraries) : "";

				var bitcodeFiles = GetBitcodeFilesParams();
				var bitcodeFilesParams = dynamicLibraries.Any() ? string.Join(" ", bitcodeFiles.Select(f => $"\"--native-lib={AlignPath(f)}\"")) : "";

				if(_runtimeExecutionMode != RuntimeExecutionMode.Interpreter && GenerateAOTProfile)
				{
					Log.LogMessage($"Forcing Interpreter mode because GenerateAOTProfile is set");
					_runtimeExecutionMode = RuntimeExecutionMode.Interpreter;
				}

				var aotMode = _runtimeExecutionMode switch
				{
					RuntimeExecutionMode.FullAOT => "--aot",
					RuntimeExecutionMode.InterpreterAndAOT => $"--aot-interp {mixedModeAotAssembliesParam}",
					RuntimeExecutionMode.Interpreter => "",
					_ => throw new NotSupportedException($"Mode {_runtimeExecutionMode} is not supported"),
				};

				var aotOptions = $"{aotMode} {dynamicLibraryParams} {bitcodeFilesParams} --emscripten-sdkdir=\"{emsdkPath}\" --builddir=\"{AlignPath(workAotPath)}\"";

				if (EnableEmccProfiling)
				{
					extraEmccFlags.Add("--profiling");
					packagerParams.Add("--no-native-strip");
				}

				var extraEmccFlagsPararm = string.Join(" ", extraEmccFlags).Replace("\\", "\\\\");

				packagerParams.Add(debugOption);
				packagerParams.Add(monovmparams);
				packagerParams.Add("--zlib");
				packagerParams.Add("--enable-fs ");
				packagerParams.Add($"--extra-emccflags=\"{extraEmccFlagsPararm} ");
				packagerParams.Add("-lidbfs.js\" ");
				packagerParams.Add(appDirParm);
				packagerParams.Add($"--runtime-config={RuntimeConfiguration} ");
				packagerParams.Add(aotOptions);
				packagerParams.Add(aotProfile);
				packagerParams.Add(MonoILLinker ? "--linker --link-mode=all" : "");
				packagerParams.Add(referencePathsParameter);
				packagerParams.Add(GenerateAOTProfile ? "--profile=aot" : "");
				packagerParams.Add($"\"{AlignPath(Path.GetFullPath(Assembly))}\"");

				var aotPackagerResult = RunProcess(packagerBinPath, string.Join(" ", packagerParams), _workDistPath);

				if (aotPackagerResult.exitCode != 0)
				{
					throw new Exception("Failed to generate wasm layout (More details are available in diagnostics mode or using the MSBuild /bl switch)");
				}

				var ninjaResult = RunProcess("ninja", "", workAotPath);

				if (ninjaResult.exitCode != 0)
				{
					throw new Exception("Failed to generate AOT layout (More details are available in diagnostics mode or using the MSBuild /bl switch)");
				}
			}
			else
			{
				if (ForceDisableWSL)
				{
					Log.LogWarning(
						"WARNING: WebAssembly emscripten packaging has been explicitly disabled through" +
						" WasmShellForceDisableWSL, the resulting compilation may not run properly.");
				}

				//
				// Run the IL Linker on the interpreter based output, as the packager does not yet do it.
				//
				if (
					MonoILLinker
				)
				{
					LinkerSetup();

					string linkerInput = Path.Combine(IntermediateOutputPath, "linker-in");
					if (Directory.Exists(linkerInput))
					{
						Directory.Delete(linkerInput, true);
					}

					Directory.Move(_managedPath, linkerInput);
					DirectoryCreateDirectory(_managedPath);

					var assemblyPath = Path.Combine(linkerInput, Path.GetFileName(Assembly));

					var frameworkBindings = new List<string>();

					if (!IsNetCoreWasm)
					{
						frameworkBindings.Add("WebAssembly.Bindings.dll");
						frameworkBindings.Add("System.Net.Http.WebAssemblyHttpHandler.dll");
						frameworkBindings.Add("WebAssembly.Net.WebSockets.dll");
					}

					var linkerSearchPaths = string.Join(" ", _referencedAssemblies.Select(Path.GetDirectoryName).Distinct().Select(p => $"-d \"{p}\" "));
					var fullSDKFolder = IsNetCoreWasm ? $"-d \"{_bclPath}\" {linkerSearchPaths}" : "";

					var bindingsPath = string.Join(" ", frameworkBindings.Select(a => $"-a \"{Path.Combine(linkerInput, a)}\""));
					bindingsPath += $" -a \"{releaseTimeZoneData}\"";

					// Opts should be aligned with the monolinker call in packager.cs, validate for linker_args as well
					var packagerLinkerOpts = $"--deterministic --disable-opt unreachablebodies --used-attrs-only true ";

					if (!IsNetCoreWasm) {
						packagerLinkerOpts += "--exclude-feature com --exclude-feature remoting --exclude-feature etw  -l none ";
					}

					var linkerResults = RunProcess(
						_linkerBinPath,
						$"-out \"{_managedPath}\" --verbose -b true {packagerLinkerOpts} -a \"{assemblyPath}\" {bindingsPath} -c link -p copy \"WebAssembly.Bindings\" -p copy \"Uno.Wasm.TimezoneData\" -d \"{_managedPath}\" {fullSDKFolder}",
						_managedPath
					);

					if (linkerResults.exitCode != 0)
					{
						throw new Exception("Failed to execute the linker");
					}

					//
					// The linker removes files after the mono-config.js file has been 
					// generated by the packager. Synchronize the list with the actual list.
					//
					var deletedFiles = Directory
						.GetFiles(linkerInput)
						.Select(Path.GetFileName)
						.Except(Directory
							.GetFiles(_managedPath)
							.Select(Path.GetFileName)
						);

					string monoConfigFilePath = Path.Combine(_workDistPath, "mono-config.js");
					var monoConfig = File.ReadAllText(monoConfigFilePath);

					foreach (var deletedFile in deletedFiles)
					{
						Log.LogMessage($"Removing linker deleted file [{deletedFile}] from mono-config.js");
						monoConfig = monoConfig
							.Replace($"\"{deletedFile}\",", "")
							.Replace($"\"{deletedFile}\"", "");
					}

					File.WriteAllText(monoConfigFilePath, monoConfig);
				}
			}
		}

		/// <summary>
		/// Applies a temporary workaround for https://github.com/mono/mono/issues/19824
		/// </summary>
		private void TryAdjustAOTProfile()
		{
			var profilePath = AotProfile?.FirstOrDefault()?.GetMetadata("FullPath");

			if (profilePath != null)
			{
				var span = File.ReadAllBytes(profilePath).AsSpan();

				var replacedMethods = new[] { "LoadIntoBufferAsync" };

				foreach (var method in replacedMethods)
				{
					var searchSpan = Encoding.ASCII.GetBytes(method).AsSpan();

					for (int i = 0; i < span.Length - searchSpan.Length; i++)
					{
						if (span[i] == searchSpan[0])
						{
							if (span.Slice(i, searchSpan.Length).SequenceEqual(searchSpan))
							{
								span[i] = (byte)'_';
							}
						}
					}
				}

				File.WriteAllBytes(profilePath, span.ToArray());
			}
		}

		private void LinkerSetup()
		{
			if (IsNetCoreWasm)
			{
				_linkerBinPath = CustomLinkerPath ?? Path.Combine(MonoWasmSDKPath, "tools", "illink.dll");
			}
			else
			{
				_linkerBinPath = CustomLinkerPath ?? Path.Combine(MonoWasmSDKPath, "wasm-bcl", "wasm_tools", "monolinker.exe");
			}

			var configFilePath = _linkerBinPath + ".config";
			if (!File.Exists(configFilePath))
			{
				var content = @"
<?xml version=""1.0"" encoding=""utf-16"" ?>
<configuration>
  <runtime>
    <AppContextSwitchOverrides value=""Switch.System.IO.UseLegacyPathHandling=false;Switch.System.IO.BlockLongPaths=false"" />
  </runtime>
</configuration>
";
				// Enable long path support for the linker
				File.WriteAllText(configFilePath, content, Encoding.Unicode);
			}
		}

		private bool IsRuntimeAOT() => _runtimeExecutionMode == RuntimeExecutionMode.FullAOT || _runtimeExecutionMode == RuntimeExecutionMode.InterpreterAndAOT;

		private string TryConvertLongPath (string path)
			=> Environment.OSVersion.Platform == PlatformID.Win32NT
				&& !string.IsNullOrEmpty(path)
				&& !path.StartsWith(@"\\?\")
				&& Path.IsPathRooted(path)
				&& EnableLongPathSupport
				&& FileInfoExtensions.PlatformRequiresLongPathNormalization
				? @"\\?\" + path
				: path;

		private string ValidateEmscripten()
		{
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
			{
				var emsdkHostFolder = Environment.GetEnvironmentVariable("WASMSHELL_WSLEMSDK") ?? "$HOME/.uno/emsdk";
				var emsdkBaseFolder = emsdkHostFolder + $"/emsdk-{Constants.EmscriptenMinVersion}";

				if (!File.Exists(Environment.GetEnvironmentVariable("WINDIR") + "\\sysnative\\bash.exe"))
				{
					throw new InvalidCastException("The Windows Subsystem for Linux is not installed, please install Ubuntu 18.04 by visiting https://docs.microsoft.com/en-us/windows/wsl/install-win10.");
				}

				var emscriptenSetupScript = Path.Combine(BuildTaskBasePath, "scripts", "emscripten-setup.sh");

				// Adjust line endings
				AdjustFileLineEndings(emscriptenSetupScript);

				var result = RunProcess(
					emscriptenSetupScript,
					$"\"{emsdkHostFolder.Replace("\\\\?\\", "").TrimEnd('\\')}\" {Constants.EmscriptenMinVersion}");

				if (result.exitCode == 0)
				{
					return emsdkBaseFolder + $"/emsdk";
				}

				var dotnetSetupScript = Path.Combine(BuildTaskBasePath, "scripts", "dotnet-setup.sh");
				AdjustFileLineEndings(dotnetSetupScript);

				Log.LogError(
					$"The Windows Subsystem for Linux dotnet environment may not be properly setup, and you may need to run " +
					$"the environment setup script. Open an Ubuntu WSL shell and run:\n\nbash -c `wslpath \"{dotnetSetupScript}\"`\n\n");

				throw new InvalidOperationException($"Failed to setup WSL environment.");
			}
			else if (Environment.OSVersion.Platform == PlatformID.Unix)
			{
				var home = Environment.GetEnvironmentVariable("HOME");
				var emsdkHostFolder = Environment.GetEnvironmentVariable("WASMSHELL_WSLEMSDK") ?? $"{home}/.uno/emsdk";
				var emscriptenSetupScript = Path.Combine(BuildTaskBasePath, "scripts", "emscripten-setup.sh");
				var emsdkBaseFolder = emsdkHostFolder + $"/emsdk-{Constants.EmscriptenMinVersion}";

				// Adjust line endings
				AdjustFileLineEndings(emscriptenSetupScript);

				var result = RunProcess(
					"bash",
					$"-c \"chmod +x {emscriptenSetupScript}; {emscriptenSetupScript} \\\"{emsdkHostFolder}\\\" {Constants.EmscriptenMinVersion}\"");

				if (result.exitCode == 0)
				{
					return emsdkBaseFolder + $"/emsdk";
				}

				throw new NotSupportedException($"Failed to install emscripten");
			}
			else
			{
				throw new NotSupportedException($"Unsupported platform");
			}
		}

		private static void AdjustFileLineEndings(string emscriptenSetupScript)
			=> File.WriteAllText(emscriptenSetupScript, File.ReadAllText(emscriptenSetupScript).Replace("\r\n", "\n"));

		private IEnumerable<string> GetDynamicLibrariesParams() =>
			GetBitcodeFilesParams().Select(Path.GetFileNameWithoutExtension);

		private IEnumerable<string> GetBitcodeFilesParams()
		{
			_bitcodeFilesCache ??= Assets
				?.Where(a => a.ItemSpec.EndsWith(".bc") || a.ItemSpec.EndsWith(".a"))
				.Where(a => !bool.TryParse(a.GetMetadata("UnoAotCompile"), out var compile) || compile)
				.Select(a => GetFilePaths(a).fullPath)
				.ToArray()
				?? new string[0];

			return _bitcodeFilesCache;
		}

		private void ParseProperties()
		{
			ParseEnumProperty(nameof(WasmShellMode), WasmShellMode, out _shellMode);
			ParseEnumProperty(nameof(MonoRuntimeExecutionMode), MonoRuntimeExecutionMode, out _runtimeExecutionMode);
			AotProfile ??= new ITaskItem[0];
			_contentExtensionsToExclude =
				ContentExtensionsToExclude
					?.Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries)
				?? new string[0];

			Log.LogMessage($"Ignoring content files with following extensions:\n\t{string.Join("\n\t", _contentExtensionsToExclude)}");
		}

		private void BuildReferencedAssembliesList()
		{
			if (ReferencePath != null)
			{
				_bclAssemblies = _bclAssemblies ?? throw new Exception("_bclAssemblies is not yet defined");

				foreach (var referencePath in ReferencePath)
				{
					var isReferenceAssembly = referencePath.GetMetadata("PathInPackage")?.StartsWith("ref/", StringComparison.OrdinalIgnoreCase) ?? false;
					var hasConcreteAssembly = isReferenceAssembly && ReferencePath.Any(innerReference => HasConcreteAssemblyForReferenceAssembly(innerReference, referencePath));

					if (isReferenceAssembly && hasConcreteAssembly)
					{
						// Reference assemblies may be present along with the actual assemblies.
						// Filter out those assemblies as they cannot be used at runtime.
						continue;
					}

					var name = Path.GetFileName(referencePath.ItemSpec);
					if (
						_bclAssemblies.ContainsKey(name)

						// NUnitLite is a particular case, as it is distributed
						// as part of the mono runtime BCL, which prevents the nuget
						// package from overriding it. We exclude it here, and cache the
						// proper assembly in the resolver farther below, so that it gets 
						// picked up first.
						&& name != "nunitlite.dll"
					)
					{
						_referencedAssemblies.Add(_bclAssemblies[name]);
					}
					else
					{
						_referencedAssemblies.Add(referencePath.ItemSpec);
					}
				}
			}
		}

		private static bool HasConcreteAssemblyForReferenceAssembly(ITaskItem other, ITaskItem referenceAssembly)
			=> Path.GetFileName(other.ItemSpec) == Path.GetFileName(referenceAssembly.ItemSpec) && (other.GetMetadata("PathInPackage")?.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) ?? false);

		private void PrepareFinalDist()
		{
			IEnumerable<byte> ComputeHash(string file)
			{
				using var hashFunction = SHA1.Create();
				using var s = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
				return hashFunction.ComputeHash(s);
			}

			var allBytes = Directory.GetFiles(_workDistPath, "*.*", SearchOption.AllDirectories)
				.AsParallel()
				.OrderBy(s => s)
				.Select(ComputeHash)
				.SelectMany(h => h)
				.ToArray();

			using var hashFunction = SHA1.Create();
			var hash = string.Join("", hashFunction.ComputeHash(allBytes).Select(b => b.ToString("x2")));

			OutputDistPath = _distPath;

			if (_shellMode == ShellMode.Node)
			{ 
				_remoteBasePackagePath = "";
				_finalPackagePath = _distPath;
				OutputPackagePath = _distPath;
			}
			else
			{
				_remoteBasePackagePath = $"package_{hash}";
				_finalPackagePath = TryConvertLongPath(Path.Combine(_distPath, _remoteBasePackagePath));
				OutputPackagePath = _finalPackagePath.Replace(@"\\?\", "");
			}

			// Create the path if it does not exist (particularly if the path is
			// not in a set of folder that exists)
			Directory.CreateDirectory(_finalPackagePath);

			if (Directory.Exists(_distPath))
			{
				Directory.Delete(_distPath, true);
			}

			try
			{
				Directory.Move(_workDistRootPath, _distPath);
			}
			catch (Exception ex)
			{
				throw new ApplicationException($"Unable to move ROOT DIST {_workDistRootPath} to {_distPath}: {ex}", ex);
			}

			try
			{
				if (!_workDistRootPath.Equals(_workDistPath))
				{
					Directory.Move(_workDistPath, _finalPackagePath);
				}
			}
			catch (Exception ex)
			{
				throw new ApplicationException($"Unable to move PACKAGE DIST {_workDistPath} to {_finalPackagePath}: {ex}", ex);
			}

			RenameFiles(_finalPackagePath, "dll");
		}

		private static void MoveFileSafe(string source, string target)
		{
			if (File.Exists(source) && source != target)
			{
				if (File.Exists(target))
				{
					File.Delete(target);
				}

				File.Move(source, target);
			}
		}


		/// <summary>
		/// Renames the files to avoid quarantine by antivirus software such as Symantec, 
		/// which are quite present in the enterprise space.
		/// </summary>
		/// <param name="extension">The extension to rename</param>
		private void RenameFiles(string path, string extension)
		{
			foreach (var dllFile in Directory.GetFiles(path, "*." + extension, SearchOption.AllDirectories))
			{
				string destDirName = Path.Combine(Path.GetDirectoryName(dllFile), Path.GetFileNameWithoutExtension(dllFile) + "." + AssembliesFileExtension);

				Log.LogMessage($"Renaming {dllFile} to {destDirName}");
				Directory.Move(dllFile, destDirName);
			}
		}

		private void GetBcl()
		{
			if (IsNetCoreWasm)
			{
				_bclPath = Path.Combine(MonoWasmSDKPath, "runtimes", "browser-wasm", "lib", "net6.0");
				var reals = Directory.GetFiles(_bclPath, "*.dll");
				_bclAssemblies = reals.ToDictionary(x => Path.GetFileName(x));
			}
			else
			{
				_bclPath = Path.Combine(MonoWasmSDKPath, "wasm-bcl", "wasm");
				var reals = Directory.GetFiles(_bclPath, "*.dll");
				var facades = Directory.GetFiles(Path.Combine(_bclPath, "Facades"), "*.dll");
				var allFiles = reals.Concat(facades);
				_bclAssemblies = allFiles.ToDictionary(x => Path.GetFileName(x));
			}
		}

		private void CreateDist()
		{
			_distPath = TryConvertLongPath(Path.GetFullPath(DistPath));
			_workDistPath = TryConvertLongPath(Path.Combine(IntermediateOutputPath, "dist_work"));
			_workDistRootPath =
				_shellMode == ShellMode.Node
				? _workDistPath
				: TryConvertLongPath(Path.Combine(IntermediateOutputPath, "dist_root_work"));
			_managedPath = Path.Combine(_workDistPath, "managed");

			if (Directory.Exists(_workDistPath))
			{
				Directory.Delete(_workDistPath, true);
			}

			if (Directory.Exists(_workDistRootPath))
			{
				Directory.Delete(_workDistRootPath, true);
			}

			Log.LogMessage($"Creating managed path {_managedPath}");
			DirectoryCreateDirectory(_managedPath);
		}

		private void CopyRuntime()
		{
			DirectoryCreateDirectory(_workDistRootPath);

			if (IsNetCoreWasm)
			{
				var runtimePath = Path.Combine(MonoWasmSDKPath, "runtimes", "browser-wasm", "native");

				foreach (var sourceFile in Directory.EnumerateFiles(runtimePath))
				{
					var dest = Path.Combine(_workDistPath, Path.GetFileName(sourceFile));
					Log.LogMessage($"Runtime: {sourceFile} -> {dest}");
					FileCopy(sourceFile, dest, true);
				}
			}
			else
			{
				// Adjust for backward compatibility
				RuntimeConfiguration = RuntimeConfiguration == "release-dynamic" ? "dynamic-release" : RuntimeConfiguration;

				var runtimePath = Path.Combine(MonoWasmSDKPath, "builds", RuntimeConfiguration.ToLower());

				foreach (var sourceFile in Directory.EnumerateFiles(runtimePath))
				{
					var dest = Path.Combine(_workDistPath, Path.GetFileName(sourceFile));

					var matchedExtension = _contentExtensionsToExclude
						.FirstOrDefault(x => sourceFile.EndsWith(x, StringComparison.OrdinalIgnoreCase));

					if (matchedExtension == null)
					{
						Log.LogMessage($"Runtime: {sourceFile} -> {dest}");
						FileCopy(sourceFile, dest, true);
					}
					else
					{
						Log.LogMessage($"Runtime: ignoring file {sourceFile} / matched with exclusion extension {matchedExtension}");
					}
				}

				FileCopy(Path.Combine(MonoWasmSDKPath, "server.py"), Path.Combine(_workDistRootPath, "server.py"), true);
			}
		}

		private (string fullPath, string relativePath) GetFilePaths(ITaskItem item)
		{
			// This is for project-local defined content
			var baseSourceFile = item.GetMetadata("DefiningProjectDirectory");

			if (item.GetMetadata("Link") is { } link && !string.IsNullOrEmpty(link))
			{
				var fullPath = Path.IsPathRooted(item.ItemSpec) ? item.ItemSpec : Path.Combine(baseSourceFile, item.ItemSpec);

				// This case is mainly for shared projects and files out of the baseSourceFile path
				return (fullPath, link);
			}
			else if (item.GetMetadata("FullPath") is { } fullPath && File.Exists(fullPath))
			{
				var sourceFilePath = item.ToString();

				if (sourceFilePath.StartsWith(CurrentProjectPath))
				{
					// This is for files added explicitly through other targets (e.g. Microsoft.TypeScript.MSBuild)
					return (fullPath: fullPath, sourceFilePath.Replace(CurrentProjectPath + Path.DirectorySeparatorChar, ""));
				}
				else
				{
					return (fullPath, sourceFilePath);
				}
			}
			else
			{
				return (Path.Combine(baseSourceFile, item.ItemSpec), item.ToString());
			}
		}

		private void CopyContent()
		{
			var assets = new List<string>();

			if (Assets != null)
			{
				foreach (var sourceFile in Assets)
				{

					var (fullSourcePath, relativePath) = GetFilePaths(sourceFile);

					// Files in "wwwroot" folder will get deployed to root by default
					var defaultDeployMode = relativePath.Contains(_wwwwroot) ? DeployMode.Root : DeployMode.Package;
					var deployModeSource = "Default";

					var matchedExtension = _contentExtensionsToExclude
						.FirstOrDefault(x => relativePath.EndsWith(x, StringComparison.OrdinalIgnoreCase));
					if (matchedExtension != null)
					{
						defaultDeployMode = DeployMode.None;
						deployModeSource = "Excluded extension";
					}

					relativePath = FixupPath(relativePath).Replace(_wwwwroot, "");

					if (relativePath.StartsWith(WasmScriptsFolder))
					{
						// Skip by default the WasmScript folder files that may
						// have been added as content files.
						// This can happen when running the TypeScript compiler.
						defaultDeployMode = DeployMode.None;
					}

					var deployToRootMetadata = sourceFile.GetMetadata(DeployMetadataName);

					if (Enum.TryParse<DeployMode>(deployToRootMetadata, out var deployMode))
					{
						deployModeSource = "Metadata";
					}
					else
					{
						deployMode = defaultDeployMode;
					}

					if (deployMode == DeployMode.Package)
					{
						// Add the file to the package assets manifest
						assets.Add(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
					}

					var dest = deployMode
						switch
						{
							DeployMode.Package => Path.Combine(_workDistPath, relativePath),
							DeployMode.Root => Path.Combine(_workDistRootPath, relativePath),
							_ => default // None or unknown mode
						};


					Log.LogMessage($"ContentFile {fullSourcePath} -> {dest ?? "<null>"} [Mode={deployMode} / by {deployModeSource}]");

					if (dest != null)
					{
						DirectoryCreateDirectory(Path.GetDirectoryName(dest));

						FileCopy(fullSourcePath, dest, true);
					}
				}
			}

			File.WriteAllLines(Path.Combine(_workDistPath, "uno-assets.txt"), assets);
		}

		private void ExtractAdditionalJS()
		{
			BuildResourceSearchList();

			var q = EnumerateResources("js", "WasmDist")
				.Concat(EnumerateResources("js", WasmScriptsFolder));

			foreach (var (name, source, resource) in q)
			{
				if (source.Name.Name != GetType().Assembly.GetName().Name)
				{
					_dependencies.Add(name);
				}

				CopyResourceToOutput(name, resource);

				Log.LogMessage($"Additional JS {name}");
			}
		}

		private void ExtractAdditionalCSS()
		{
			BuildResourceSearchList();

			var q = EnumerateResources("css", "WasmCSS");

			foreach (var (name, source, resource) in q)
			{
				using (var srcs = resource.GetResourceStream())
				{
					CopyResourceToOutput(name, resource);

					Log.LogMessage($"Additional CSS {name}");
				}
			}

			_additionalStyles = q
				.Select(res => res.name)
				.ToArray();
		}

		private void BuildResourceSearchList()
		{
			if (_resourceSearchList == null)
			{
				var sourceList = new List<string> {
					// Add the boostrapper assembly first, so the css defined there can be overriden.
					GetType().Assembly.Location
				};

				sourceList.AddRange(_referencedAssemblies);

				// Add the main assembly last so it can have a final say
				sourceList.Add(Assembly);

				_resourceSearchList ??= new List<AssemblyDefinition>(
					from asmPath in sourceList.Distinct()
					let asm = ReadAssembly(asmPath)
					where asm != null
					select asm
				);
			}

			AssemblyDefinition? ReadAssembly(string asmPath)
			{
				try
				{
					return AssemblyDefinition.ReadAssembly(asmPath);
				}
				catch (Exception ex)
				{
					Log.LogMessage($"Failed to read assembly {ex}");
					return null;
				}
			}
		}

		private void CopyResourceToOutput(string name, EmbeddedResource resource)
		{
			var dest = Path.Combine(_workDistPath, name);

			using (var srcs = resource.GetResourceStream())
			{
				using (var dests = new FileStream(dest, FileMode.Create, FileAccess.Write))
				{
					srcs.CopyTo(dests);
				}
			}
		}

		private IEnumerable<(string name, AssemblyDefinition source, EmbeddedResource resource)> EnumerateResources(string extension, string folder)
		{
			var fullExtension = "." + extension;
			var fullFolder = "." + folder + ".";

			return from asm in _resourceSearchList
				   from res in asm.MainModule.Resources.OfType<EmbeddedResource>()
				   where res.Name.EndsWith(fullExtension)
				   where res.Name.Contains(fullFolder)
				   select (
					name: res.Name.Substring(res.Name.IndexOf(fullFolder) + fullFolder.Length),
					source: asm,
					resource: res
					);
		}

		private MethodDefinition DiscoverEntryPoint()
		{
			var asm = AssemblyDefinition.ReadAssembly(Assembly);

			if (asm?.EntryPoint is MethodDefinition def)
			{
				return def;
			}

			throw new Exception($"{Path.GetFileName(Assembly)} is missing an entry point. Add <OutputType>Exe</OutputType> in the project file and a static main.");
		}

		private void GenerateConfig()
		{
			var unoConfigJsPath = Path.Combine(_finalPackagePath, "uno-config.js");

			using (var w = new StreamWriter(unoConfigJsPath, false, new UTF8Encoding(false)))
			{
				var dependencies = string.Join(", ", _dependencies.Select(x => $"\"./{_remoteBasePackagePath}/{Path.GetFileNameWithoutExtension(x)}\""));
				var entryPoint = DiscoverEntryPoint();

				var config = new StringBuilder();

				var (monoWasmFileName, monoWasmSize, totalAssembliesSize, assemblyFiles, filesIntegrity) = GetFilesDetails();
				var assembliesSize = string.Join(
					",",
					assemblyFiles.Select(ass => $"\"{ass.fileName}\":{ass.length}"));
				var filesIntegrityStr = string.Join(
					",",
					filesIntegrity.Select(f => $"\"{f.fileName}\":\"{f.integrity}\""));

				var enablePWA = !string.IsNullOrEmpty(PWAManifestFile);
				var offlineFiles = enablePWA ? string.Join(", ", GetPWACacheableFiles().Select(f => $"\".{f}\"")) : "";

				config.AppendLine($"config.uno_remote_managedpath = \"{ Path.GetFileName(_managedPath) }\";");
				config.AppendLine($"config.uno_app_base = \"{ _remoteBasePackagePath }\";");
				config.AppendLine($"config.uno_dependencies = [{dependencies}];");
				config.AppendLine($"config.uno_main = \"[{entryPoint.DeclaringType.Module.Assembly.Name.Name}] {entryPoint.DeclaringType.FullName}:{entryPoint.Name}\";");
				config.AppendLine($"config.assemblyFileExtension = \"{AssembliesFileExtension}\";");
				config.AppendLine($"config.mono_wasm_runtime = \"{monoWasmFileName}\";");
				config.AppendLine($"config.mono_wasm_runtime_size = {monoWasmSize};");
				config.AppendLine($"config.assemblies_with_size = {{{assembliesSize}}};");
				config.AppendLine($"config.files_integrity = {{{filesIntegrityStr}}};");
				config.AppendLine($"config.total_assemblies_size = {totalAssembliesSize};");
				config.AppendLine($"config.enable_pwa = {enablePWA.ToString().ToLowerInvariant()};");
				config.AppendLine($"config.offline_files = ['./', {offlineFiles}];");

				if (GenerateAOTProfile)
				{
					config.AppendLine($"config.generate_aot_profile = true;");
				}

				config.AppendLine($"config.environmentVariables = config.environmentVariables || {{}};");

				void AddEnvironmentVariable(string name, string value) => config.AppendLine($"config.environmentVariables[\"{name}\"] = \"{value}\";");

				if (MonoEnvironment != null)
				{
					foreach (var env in MonoEnvironment)
					{
						AddEnvironmentVariable(env.ItemSpec, env.GetMetadata("Value"));
					}
				}

				var isProfiledAOT = HasAotProfile && _runtimeExecutionMode == RuntimeExecutionMode.InterpreterAndAOT;

				AddEnvironmentVariable("UNO_BOOTSTRAP_MONO_RUNTIME_MODE", _runtimeExecutionMode.ToString());
				AddEnvironmentVariable("UNO_BOOTSTRAP_MONO_PROFILED_AOT", isProfiledAOT.ToString());
				AddEnvironmentVariable("UNO_BOOTSTRAP_LINKER_ENABLED", MonoILLinker.ToString());
				AddEnvironmentVariable("UNO_BOOTSTRAP_DEBUGGER_ENABLED", RuntimeDebuggerEnabled.ToString());
				AddEnvironmentVariable("UNO_BOOTSTRAP_MONO_RUNTIME_CONFIGURATION", RuntimeConfiguration);
				AddEnvironmentVariable("UNO_BOOTSTRAP_APP_BASE", _remoteBasePackagePath);

				w.Write(config.ToString());
			}
		}

		private void MergeConfig()
		{
			if(_shellMode == ShellMode.Node)
			{
				var tempFile = Path.GetTempFileName();
				try
				{
					var monoJsPath = Path.Combine(_finalPackagePath, "dotnet.js");

					using (var fs = new StreamWriter(tempFile))
					{
						fs.Write(File.ReadAllText(Path.Combine(_finalPackagePath, "mono-config.js")));
						fs.Write(File.ReadAllText(Path.Combine(_finalPackagePath, "uno-config.js")));
						fs.Write(File.ReadAllText(Path.Combine(_finalPackagePath, "uno-bootstrap.js")));
						fs.Write(File.ReadAllText(monoJsPath));
					}

					File.Delete(monoJsPath);
					File.Move(tempFile, monoJsPath);

					Log.LogMessage($"Merged config files with dotnet.js");
				}
				finally
				{
					try
					{
						if (File.Exists(tempFile))
						{
							File.Delete(tempFile);
						}
					}
					catch(Exception e)
					{
						Console.WriteLine($"Failed to delete temporary file: {e}");
					}
				}
			}
		}

		private IEnumerable<string> GetPWACacheableFiles()
			=> from file in Directory.EnumerateFiles(_finalPackagePath, "*.*", SearchOption.AllDirectories)
			   where !file.EndsWith("web.config", StringComparison.OrdinalIgnoreCase)
			   select file.Replace(_distPath, "").Replace("\\", "/");

		private static readonly SHA384Managed _sha384 = new SHA384Managed();
		
		private (string monoWasmFileName, long monoWasmSize, long totalAssembliesSize, (string fileName, long length)[] assemblyFiles, (string fileName, string integrity)[] filesIntegrity) GetFilesDetails()
		{
			const string monoWasmFileName = "dotnet.wasm";

			var monoWasmFilePathAndName = Path.Combine(_finalPackagePath, monoWasmFileName);
			var monoWasmSize = new FileInfo(monoWasmFilePathAndName).Length;

			var assemblyPathAndFiles = Directory
				.EnumerateFiles(Path.Combine(_finalPackagePath, "managed"), "*." + AssembliesFileExtension, SearchOption.TopDirectoryOnly)
				.ToArray();

			var assemblyFiles = assemblyPathAndFiles
				.Select(f =>
				{
					var fi = new FileInfo(f);
					return (fileName: fi.Name, length: fi.Length);
				})
				.ToArray();

			var totalAssembliesSize = assemblyFiles.Sum(fi => fi.length);

			(string fileName, string integrity)[] filesIntegrity;
			if (UseFileIntegrity)
			{
				var integrities = new List<(string fileName, string integrity)>();

				void AddFileIntegrity(string filePath)
				{
					var bytes = File.ReadAllBytes(filePath);
					var hash = _sha384.ComputeHash(bytes);
					var integrity = Convert.ToBase64String(hash);
					var path = filePath.Replace(_distPath, "").Replace("\\", "/");

					integrities.Add(($".{path}", $"sha384-{integrity}"));
				}

				AddFileIntegrity(monoWasmFilePathAndName);

				foreach (var f in assemblyPathAndFiles)
				{
					AddFileIntegrity(f);
				}


				filesIntegrity = integrities.ToArray();
			}
			else
			{
				filesIntegrity = new (string fileName, string integrity)[0];
			}

			return ($"./{_remoteBasePackagePath}/" + monoWasmFileName, monoWasmSize, totalAssembliesSize, assemblyFiles, filesIntegrity);
		}

		private void GenerateHtml()
		{
			if (_shellMode != ShellMode.Browser)
			{
				return;
			}

			var htmlPath = Path.Combine(_distPath, "index.html");

			using (var w = new StreamWriter(htmlPath, false, new UTF8Encoding(false)))
			{
				using (var reader = new StreamReader(IndexHtmlPath))
				{
					var html = reader.ReadToEnd();

					var styles = string.Join("\r\n", _additionalStyles.Select(s => $"<link rel=\"stylesheet\" type=\"text/css\" href=\"./{s}\" />"));
					html = html.Replace("$(ADDITIONAL_CSS)", styles);

					var extraBuilder = new StringBuilder();
					GeneratePWAContent(extraBuilder);
					GeneratePrefetchHeaderContent(extraBuilder);

					html = html.Replace("$(ADDITIONAL_HEAD)", extraBuilder.ToString());

					// Compatibility after the change from mono.js to dotnet.js
					html = html.Replace("mono.js\"", "dotnet.js\"");
					html = html.Replace("\"./", $"\"./{_remoteBasePackagePath}/");

					w.Write(html);

					Log.LogMessage($"HTML {htmlPath}");
				}
			}
		}

		private void GeneratePrefetchHeaderContent(StringBuilder extraBuilder)
		{
			if (_shellMode == ShellMode.Browser && GeneratePrefetchHeaders)
			{
				extraBuilder.AppendLine($"<link rel=\"prefetch\" href=\"./dotnet.wasm\" />");

				var distName = Path.GetFileName(_managedPath);
				foreach(var file in Directory.GetFiles(_managedPath, "*.clr", SearchOption.AllDirectories))
				{
					extraBuilder.AppendLine($"<link rel=\"prefetch\" href=\"./{distName}/{Path.GetFileName(file)}\" />");
				}
			}
		}

		private void GeneratePWAContent(StringBuilder extraBuilder)
		{
			if (_shellMode != ShellMode.Browser)
			{
				return;
			}

			if (!string.IsNullOrWhiteSpace(PWAManifestFile))
			{
				var manifestDocument = JObject.Parse(File.ReadAllText(PWAManifestFile));

				extraBuilder.AppendLine($"<link rel=\"manifest\" href=\"./{PWAManifestFile}\" />");
				extraBuilder.AppendLine($"<link rel=\"script\" href=\"./service-worker.js\" />");

				// See https://developer.apple.com/library/archive/documentation/AppleApplications/Reference/SafariHTMLRef/Articles/MetaTags.html
				extraBuilder.AppendLine($"<meta name=\"apple-mobile-web-app-capable\" content=\"yes\">");

				if (manifestDocument["icons"] is JArray array
					&& array.Where(v => v["sizes"]?.Value<string>() == "1024x1024").FirstOrDefault() is JToken img)
				{
					extraBuilder.AppendLine($"<link rel=\"apple-touch-icon\" href=\"./{img["src"]}\" />");
				}

				if (manifestDocument["theme_color"]?.Value<string>() is string color)
				{
					extraBuilder.AppendLine($"<meta name=\"theme-color\" content=\"{color}\" />");
				}
				else
				{
					extraBuilder.AppendLine($"<meta name=\"theme-color\" content=\"#fff\" />");
				}

				// Transform the PWA assets
				if(manifestDocument["icons"] is JArray icons && !string.IsNullOrWhiteSpace(_remoteBasePackagePath))
				{
					foreach(var icon in icons)
					{
						var originalSource = icon["src"]?.Value<string>();

						icon["src"] = originalSource switch
						{
							string s when s.StartsWith("./") => $"./{_remoteBasePackagePath}/" + s.Substring(2),
							string s => $"{_remoteBasePackagePath}/" + s,
							_ => originalSource
						};
					}
				}

				File.WriteAllText(PWAManifestFile, manifestDocument.ToString());
			}
		}

		private void ParseEnumProperty<TEnum>(string name, string stringValue, out TEnum value) where TEnum : struct
		{
			if (Enum.TryParse<TEnum>(stringValue, true, out value))
			{
				Log.LogMessage(MessageImportance.Low, $"{name}={value}");
			}
			else
			{
				throw new NotSupportedException($"The {name} {value} is not supported");
			}
		}
	}
}
