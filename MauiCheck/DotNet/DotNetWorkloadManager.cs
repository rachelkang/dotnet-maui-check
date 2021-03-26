﻿using Microsoft.DotNet.MSBuildSdkResolver;
using Microsoft.NET.Sdk.WorkloadManifestReader;
using NuGet.Common;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Polly;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotNetCheck.DotNet
{
	public class DotNetWorkloadManager
	{
		public DotNetWorkloadManager(string sdkRoot, string sdkVersion, params string[] nugetPackageSources)
		{
			SdkRoot = sdkRoot;
			SdkVersion = sdkVersion;
			NuGetPackageSources = nugetPackageSources;

			manifestProvider = new SdkDirectoryWorkloadManifestProvider(SdkRoot, SdkVersion);
			workloadResolver = WorkloadResolver.Create(manifestProvider, SdkRoot, SdkVersion);
		}

		public readonly string SdkRoot;
		public readonly string SdkVersion;

		readonly SdkDirectoryWorkloadManifestProvider manifestProvider;
		readonly WorkloadResolver workloadResolver;

		public readonly string[] NuGetPackageSources;

		public IEnumerable<string> GetInstalledWorkloads()
		{
			var workloadManifestReaderType = typeof(WorkloadResolver).Assembly.GetType("Microsoft.NET.Sdk.WorkloadManifestReader.WorkloadManifestReader");

			var themethod = workloadManifestReaderType.GetMethod("ReadWorkloadManifest", BindingFlags.Public | BindingFlags.Static, Type.DefaultBinder, new Type[] { typeof(Stream) }, null);

			var readWorkloadManifestMethods = workloadManifestReaderType.GetMethods(BindingFlags.Static | BindingFlags.Public)

				.FirstOrDefault(m => m.Name == "ReadWorkloadManifest" && (m.GetParameters()?.FirstOrDefault()?.Equals(typeof(Stream)) ?? false) && (m.GetParameters()?.Length ?? 0) == 1);

			var list = new List<object>(); // WorkloadManifest
			foreach (Stream manifest in manifestProvider.GetManifests())
			{
				using (manifest)
				{
					var workloadManifest = themethod.Invoke(null, new object[] { manifest });

					var workloadsDict = workloadManifest.GetType().GetProperty("Workloads").GetValue(workloadManifest);

					var workloadsDictKeys = workloadsDict.GetType().GetProperty("Keys").GetValue(workloadsDict) as System.Collections.ICollection;

					foreach (var key in workloadsDictKeys)
					{
						var workloadId = key.ToString();

						yield return workloadId;
					}
				}
			}
		}

		public IEnumerable<WorkloadResolver.PackInfo> GetPacksInWorkload(string workloadId)
		{
			foreach (var packId in workloadResolver.GetPacksInWorkload(workloadId))
			{
				var packInfo = workloadResolver.TryGetPackInfo(packId);
				if (packInfo != null)
					yield return packInfo;
			}
		}

		public ISet<WorkloadResolver.WorkloadInfo> GetWorkloadSuggestions(params string[] missingPackIds)
			=> workloadResolver.GetWorkloadSuggestionForMissingPacks(missingPackIds);

		public IEnumerable<WorkloadResolver.PackInfo> GetAllInstalledWorkloadPacks()
			=> workloadResolver.GetInstalledWorkloadPacksOfKind(WorkloadPackKind.Framework)
				.Concat(workloadResolver.GetInstalledWorkloadPacksOfKind(WorkloadPackKind.Library))
				.Concat(workloadResolver.GetInstalledWorkloadPacksOfKind(WorkloadPackKind.Sdk))
				.Concat(workloadResolver.GetInstalledWorkloadPacksOfKind(WorkloadPackKind.Template))
				.Concat(workloadResolver.GetInstalledWorkloadPacksOfKind(WorkloadPackKind.Tool));

		public IEnumerable<WorkloadResolver.PackInfo> GetInstalledWorkloadPacks(WorkloadPackKind kind)
			=> workloadResolver.GetInstalledWorkloadPacksOfKind(kind);

		public Task<bool> InstallWorkloadManifest(string packageId, NuGetVersion manifestPackageVersion, CancellationToken cancelToken)
		{
			var manifestRoot = GetSdkManifestRoot();
			var manifestDir = Path.Combine(manifestRoot, packageId);

			return DownloadAndInstallNuGet(packageId, manifestPackageVersion, manifestDir, cancelToken, true);
		}

		public bool TemplateExistsOnDisk(string packId, string packVersion)
		{
			var sdkTemplatePacksFolder = Path.Combine(SdkRoot, "template-packs");

			if (Directory.Exists(sdkTemplatePacksFolder)
				&& (Directory.EnumerateFiles(sdkTemplatePacksFolder, $"{packId}.{packVersion}*.nupkg", SearchOption.AllDirectories).Any()
				|| Directory.EnumerateFiles(sdkTemplatePacksFolder, $"{packId}.{packVersion.ToLowerInvariant()}*.nupkg", SearchOption.AllDirectories).Any()))
				return true;

			var userTemplateEngineDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".templateengine",
				"dotnetcli",
				$"v{SdkVersion}",
				"packages");

			if (Directory.Exists(userTemplateEngineDir)
				&& (Directory.EnumerateFiles(userTemplateEngineDir, $"{packId}.{packVersion}*.nupkg", SearchOption.AllDirectories).Any()
				|| Directory.EnumerateFiles(userTemplateEngineDir, $"{packId}.{packVersion.ToLowerInvariant()}*.nupkg", SearchOption.AllDirectories).Any()))
				return true;

			return false;
		}

		public async Task<bool> InstallWorkloadPack(string sdkRoot, Manifest.DotNetSdkPack sdkPack, CancellationToken cancelToken)
		{
			WorkloadResolver.PackInfo packInfo;

			if (sdkPack.SkipManifestCheck && NuGetVersion.TryParse(sdkPack.Version, out var packVersion))
			{
				var kind = sdkPack?.PackKind?.ToLowerInvariant() switch
				{
					"sdk" => WorkloadPackKind.Sdk,
					"framework" => WorkloadPackKind.Framework,
					"library" => WorkloadPackKind.Library,
					"template" => WorkloadPackKind.Template,
					"tool" => WorkloadPackKind.Tool,
					_ => WorkloadPackKind.Sdk
				};

				var path = kind == WorkloadPackKind.Template ?
					Path.Combine(Path.GetTempPath(), $"{sdkPack.Id}.{sdkPack.Version}.nupkg")
					: Path.Combine(sdkRoot, "sdk", $"{sdkPack.Id}", sdkPack.Version);

				packInfo = new WorkloadResolver.PackInfo(sdkPack.Id, sdkPack.Version, kind, path);
			}
			else
			{
				packInfo = workloadResolver.TryGetPackInfo(sdkPack.Id);
			}

			if (packInfo != null && NuGetVersion.TryParse(packInfo.Version, out var version))
			{
				if (packInfo.Kind == WorkloadPackKind.Template)
				{
					var templatePacksDir = Path.GetDirectoryName(packInfo.Path);
					if (!Directory.Exists(templatePacksDir))
						Directory.CreateDirectory(templatePacksDir);

					var r = await DownloadAndInstallNuGet(packInfo.Id, version, packInfo.Path, cancelToken, false);

					// Short circuit the installation into the template-packs dir since this one might not
					// be a part of any workload manifest, so we need to install with dotnet new -i
					if (sdkPack.SkipManifestCheck)
					{
						var dotnetExe = Path.Combine(sdkRoot, DotNetSdk.DotNetExeName);

						var p = new ShellProcessRunner(new ShellProcessRunnerOptions(dotnetExe, $"new -i \"{packInfo.Path}\""));
						return p.WaitForExit()?.ExitCode == 0;
					}

					return r;
				}

				var actualPackId = GetAliasToPackId(packInfo);

				if (!string.IsNullOrEmpty(actualPackId))
				{
					var packsRoot = Path.Combine(SdkRoot, "packs");
					var packPath = Path.Combine(packsRoot, actualPackId, packInfo.Version);

					if (!Directory.Exists(packPath))
						Directory.CreateDirectory(packPath);

					return await DownloadAndInstallNuGet(actualPackId, version, packPath, cancelToken, true);
				}
			}

			return false;
		}

		string GetSdkManifestRoot()
		{
			int last2DigitsTo0(int versionBuild)
				=> versionBuild / 100 * 100;

			string manifestDirectory;

			if (!Version.TryParse(SdkVersion.Split('-')[0], out var result))
				throw new ArgumentException("Invalid 'SdkVersion' version: " + SdkVersion);

			var sdkVersionBand = $"{result.Major}.{result.Minor}.{last2DigitsTo0(result.Build)}";
			var environmentVariable = Environment.GetEnvironmentVariable("DOTNETSDK_WORKLOAD_MANIFEST_ROOT");
			if (!string.IsNullOrEmpty(environmentVariable))
				manifestDirectory = environmentVariable;
			else
				manifestDirectory = Path.Combine(SdkRoot, "sdk-manifests", sdkVersionBand);

			return manifestDirectory;
		}

		string GetAliasToPackId(WorkloadResolver.PackInfo packInfo)
			=> GetAliasToPackId(packInfo.Path, packInfo.Id, packInfo.Version);

		string GetAliasToPackId(string packPath, string packId, string packVersion)
		{
			if (NuGetVersion.TryParse(packVersion, out var nugetVersion))
			{
				if (Uri.TryCreate($"file://{packPath}", UriKind.Absolute, out var pathUri))
				{
					var pathSegments = pathUri.Segments.Select(s => s.Trim('/'));

					// Check if the segment is equal to, or starts with the manifest package id
					// since the id we have might be one with an alias-to and we want the alias id instead, to restore that 
					var aliasOrId = pathSegments.FirstOrDefault(p => p.StartsWith(packId, StringComparison.OrdinalIgnoreCase));

					if (!string.IsNullOrEmpty(aliasOrId))
					{
						if (aliasOrId.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
							aliasOrId = aliasOrId.Substring(0, aliasOrId.Length - 6);
						
						return aliasOrId;
					}
				}
			}

			if (packId.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
				packId = packId.Substring(0, packId.Length - 6);

			return packId;
		}

		async Task<bool> DownloadAndInstallNuGet(string packageId, NuGetVersion packageVersion, string destination, CancellationToken cancelToken, bool extract)
		{
			var nugetCache = new SourceCacheContext();
			var nugetLogger = NullLogger.Instance;

			var tmpZipFile = new FileInfo(Path.GetTempFileName());

			foreach (var pkgSrc in NuGetPackageSources)
			{
				try
				{
					await Policy
						.Handle<ObjectDisposedException>()
						.Or<OperationCanceledException>()
						.Or<IOException>()
						.Or<InvalidDataException>()
						.RetryAsync(3)
						.ExecuteAsync(async () =>
						{
							var nugetSource = Repository.Factory.GetCoreV3(pkgSrc);

							var byIdRes = await nugetSource.GetResourceAsync<FindPackageByIdResource>();

							// Cause a retry if this is null
							if (byIdRes == null)
								throw new InvalidDataException();

							if (await byIdRes.DoesPackageExistAsync(packageId, packageVersion, nugetCache, nugetLogger, cancelToken))
							{
								using var downloader = await byIdRes.GetPackageDownloaderAsync(new PackageIdentity(packageId, packageVersion), nugetCache, nugetLogger, cancelToken);

								await downloader.CopyNupkgFileToAsync(tmpZipFile.FullName, cancelToken);

								if (tmpZipFile.Exists && tmpZipFile.Length > 0)
								{
									if (extract)
									{
										// Try and clear out the directory first in case anything old remains
										if (Directory.Exists(destination))
										{
											try { Directory.Delete(destination, true); }
											catch { }
										}

										if (!Directory.Exists(destination))
											Directory.CreateDirectory(destination);

										using var zip = Xamarin.Tools.Zip.ZipArchive.Open(tmpZipFile.FullName, FileMode.Open, destination, false);
										zip.ExtractAll(destination);

										//ZipFile.ExtractToDirectory(tmpZipFile.FullName, destination, true);
									}
									else
									{
										File.Copy(tmpZipFile.FullName, destination, true);
									}
									return true;
								}
							}

							return false;
						});

					return true;

				}
				catch (Exception ex) 
				{
					throw ex;
				}
			}

			return false;
		}
	}
}
