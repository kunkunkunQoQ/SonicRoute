using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace SonicRoute.Updates
{
    internal sealed class InstallOriginal
    {
        public string Name { get; set; } = "";
        public bool Exists { get; set; }
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
    }

    internal sealed class InstallPlan
    {
        public string Id { get; set; } = "";
        public string TargetExe { get; set; } = "";
        public string DataDirectory { get; set; } = "";
        public string CacheDirectory { get; set; } = "";
        public string Version { get; set; } = "";
        public int ParentId { get; set; }
        public long ParentStartTicks { get; set; }
        public UpdatePackage Package { get; set; } = new();
        public List<InstallOriginal> Originals { get; set; } = new();
        public string Status { get; set; } = "prepared";
        public string Error { get; set; } = "";
        internal string TargetDirectory => Path.GetDirectoryName(TargetExe)!;
        internal string WorkDirectory => Path.Combine(TargetDirectory, ".sonicroute-update-" + Id);
        internal string PlanPath => Path.Combine(CacheDirectory, "plan.json");
        internal string Runner => Path.Combine(CacheDirectory, "runner", "SonicRoute.exe");
        internal string EventName(string purpose) => @"Local\SonicRoute.Update." + Id + "." + purpose;
        internal string DestinationName(string name) => name == "SonicRoute.exe" ? Path.GetFileName(TargetExe)
            : name == "SonicRoute.exe.config" ? Path.GetFileName(TargetExe) + ".config" : name;
    }

    internal static class UpdateInstaller
    {
        internal const string MarkerName = ".sonicroute-update.pending";
        internal static string GateName(string edition) => @"Local\SonicRoute.UpdateGate." + edition;

        internal static void WritePlan(InstallPlan plan)
        {
            string path = plan.PlanPath;
            string temporary = path + ".tmp";
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, UpdateProtocol.Json));
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        internal static void WriteOutcome(InstallPlan plan)
        {
            try
            {
                string path = Path.Combine(plan.DataDirectory, "Updates", "last-result.json");
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new { plan.Status, plan.Version, plan.Error, plan.Id, plan.TargetExe }, UpdateProtocol.Json));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            catch { }
        }

        internal static InstallPlan ReadPlan(string path)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Update plan too large.");
            return JsonSerializer.Deserialize<InstallPlan>(File.ReadAllText(path), UpdateProtocol.Json) ?? throw new InvalidDataException("Empty update plan.");
        }

        internal static void ValidatePlan(InstallPlan p, string edition, string architecture)
        {
            if (!Guid.TryParseExact(p.Id, "N", out _) || !Path.IsPathRooted(p.TargetExe) || !Path.IsPathRooted(p.CacheDirectory)
                || !Path.IsPathRooted(p.DataDirectory) || UpdateProtocol.ParseVersion(p.Version) == null
                || !Path.GetExtension(p.TargetExe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid install plan.");
            if (Path.GetFullPath(p.TargetExe) != p.TargetExe || Path.GetFullPath(p.CacheDirectory) != p.CacheDirectory
                || Path.GetFullPath(p.DataDirectory) != p.DataDirectory || p.Package == null || p.Originals == null)
                throw new InvalidDataException("Non-canonical update paths.");
            string expectedCache = Path.Combine(p.DataDirectory, "Updates", "transactions", p.Id);
            if (!Path.GetFullPath(p.CacheDirectory).Equals(Path.GetFullPath(expectedCache), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update cache path.");
            if (p.TargetDirectory.Equals(Path.GetDirectoryName(p.Runner), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid update target.");
            UpdateProtocol.RejectReparsePoints(p.TargetExe);
            UpdateProtocol.RejectReparsePoints(p.CacheDirectory);
            UpdateProtocol.RejectReparsePoints(p.WorkDirectory);
            UpdateProtocol.ValidatePackage(p.Package, edition, architecture);
            if (p.Originals.Count != p.Package.Files.Count || p.Originals.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Originals.Count
                || p.Originals.Any(f => !p.Package.Files.Any(n => n.Path == f.Name) || (f.Exists && (!UpdateProtocol.IsHash(f.Sha256) || f.Size <= 0))))
                throw new InvalidDataException("Invalid backup manifest.");
        }

        internal static void StagePayload(UpdatePackage package, string payload, string incoming, string version)
        {
            UpdateProtocol.ValidatePackage(package, package.Edition, package.Architecture);
            UpdateProtocol.Verify(payload, package.Sha256, package.Size);
            Directory.CreateDirectory(incoming);
            if (package.Edition == "Lite")
                File.Copy(payload, Path.Combine(incoming, "SonicRoute.exe"));
            else
            {
                using var archive = ZipFile.OpenRead(payload);
                if (archive.Entries.Count != package.Files.Count) throw new InvalidDataException("Unexpected ZIP entries.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    var file = package.Files.SingleOrDefault(f => f.Path == entry.FullName);
                    // No directory entries, slash normalization, alternate streams, links, scripts or ZIP bombs.
                    if (file == null || !seen.Add(entry.FullName) || entry.Length != file.Size
                        || (entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                        throw new InvalidDataException("Unsafe ZIP entry.");
                    using var source = entry.Open();
                    using var destination = new FileStream(Path.Combine(incoming, file.Path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[65536];
                    long total = 0;
                    int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += count;
                        if (total > file.Size) throw new InvalidDataException("ZIP entry exceeded expected size.");
                        destination.Write(buffer, 0, count);
                    }
                    destination.Flush(true);
                }
            }
            foreach (var file in package.Files) UpdateProtocol.Verify(Path.Combine(incoming, file.Path), file.Sha256, file.Size);
            UpdateProtocol.VerifyExecutable(Path.Combine(incoming, "SonicRoute.exe"), package.Architecture, version);
            var description = FileVersionInfo.GetVersionInfo(Path.Combine(incoming, "SonicRoute.exe")).FileDescription;
            if (description != "SonicRoute " + package.Edition) throw new InvalidDataException("Wrong executable edition or unsupported update protocol.");
        }

        internal static InstallPlan Prepare(UpdateRelease release, string payload, string executable, string dataDirectory)
        {
            var package = release.Package ?? throw new InvalidOperationException("No update package.");
            using var currentProcess = Process.GetCurrentProcess();
            var plan = new InstallPlan { Id = Guid.NewGuid().ToString("N"), TargetExe = Path.GetFullPath(executable),
                DataDirectory = Path.GetFullPath(dataDirectory), Version = release.Tag, Package = package,
                ParentId = currentProcess.Id, ParentStartTicks = currentProcess.StartTime.ToUniversalTime().Ticks };
            plan.CacheDirectory = Path.Combine(plan.DataDirectory, "Updates", "transactions", plan.Id);
            UpdateProtocol.RejectReparsePoints(plan.TargetExe);
            UpdateProtocol.RejectReparsePoints(plan.CacheDirectory);
            if (File.Exists(Path.Combine(plan.TargetDirectory, MarkerName))) throw new IOException("An update needs recovery first.");
            try
            {
                // Creating staging under the actual executable directory is the write permission check.
                // No elevation, guessed installation path, or current-working-directory dependency.
                Directory.CreateDirectory(plan.WorkDirectory);
                StagePayload(package, payload, Path.Combine(plan.WorkDirectory, "incoming"), plan.Version);
                Directory.CreateDirectory(Path.Combine(plan.WorkDirectory, "backup"));
                Directory.CreateDirectory(Path.Combine(plan.CacheDirectory, "runner"));
                foreach (var file in package.Files)
                {
                    string target = Path.Combine(plan.TargetDirectory, plan.DestinationName(file.Path));
                    UpdateProtocol.RejectReparsePoints(target);
                    var old = new InstallOriginal { Name = file.Path, Exists = File.Exists(target) };
                    if (old.Exists)
                    {
                        old.Size = new FileInfo(target).Length; old.Sha256 = UpdateProtocol.Hash(target);
                        string backup = Path.Combine(plan.WorkDirectory, "backup", file.Path);
                        DurableCopy(target, backup);
                        UpdateProtocol.Verify(backup, old.Sha256, old.Size);
                    }
                    plan.Originals.Add(old);
                }
                // Execute our current local program as the helper; nothing downloaded is run as an installer.
                if (package.Edition == "Lite") DurableCopy(executable, plan.Runner);
                else
                {
                    foreach (string name in UpdateProtocol.LegacyFiles)
                    {
                        string source = Path.Combine(plan.TargetDirectory, plan.DestinationName(name));
                        if (File.Exists(source)) DurableCopy(source, Path.Combine(plan.CacheDirectory, "runner", name));
                    }
                }
                ValidatePlan(plan, package.Edition, package.Architecture);
                WritePlan(plan);
                return plan;
            }
            catch
            {
                // Only directories created for this fresh GUID are eligible for removal.
                SafeDeleteTree(plan.WorkDirectory);
                SafeDeleteTree(plan.CacheDirectory);
                throw;
            }
        }

        private static void DurableCopy(string source, string destination)
        {
            using var input = File.OpenRead(source);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output); output.Flush(true);
        }

        internal static void Apply(InstallPlan plan)
        {
            ValidatePlan(plan, plan.Package.Edition, plan.Package.Architecture);
            foreach (var old in plan.Originals)
            {
                string target = Path.Combine(plan.TargetDirectory, plan.DestinationName(old.Name));
                UpdateProtocol.RejectReparsePoints(target);
                if (old.Exists) UpdateProtocol.Verify(target, old.Sha256, old.Size);
                else if (File.Exists(target)) throw new IOException("Installation changed while preparing update.");
                var file = plan.Package.Files.Single(f => f.Path == old.Name);
                UpdateProtocol.Verify(Path.Combine(plan.WorkDirectory, "incoming", file.Path), file.Sha256, file.Size);
            }
            // Journal and marker reach disk before the first replacement. All old files already have backups.
            plan.Status = "installing"; WritePlan(plan);
            string marker = Path.Combine(plan.TargetDirectory, MarkerName);
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] id = System.Text.Encoding.ASCII.GetBytes(plan.Id);
                stream.Write(id, 0, id.Length); stream.Flush(true);
            }
            // Dependencies first, main executable last. The edition mutex prevents older clients starting.
            foreach (var file in plan.Package.Files.OrderBy(f => f.Path == "SonicRoute.exe" ? 1 : 0))
            {
                string target = Path.Combine(plan.TargetDirectory, plan.DestinationName(file.Path));
                string incoming = Path.Combine(plan.WorkDirectory, "incoming", file.Path);
                if (File.Exists(target)) File.Replace(incoming, target, null);
                else File.Move(incoming, target);
                UpdateProtocol.Verify(target, file.Sha256, file.Size);
            }
            plan.Status = "starting"; WritePlan(plan);
        }

        internal static void Rollback(InstallPlan plan)
        {
            ValidatePlan(plan, plan.Package.Edition, plan.Package.Architecture);
            plan.Status = "recovering"; WritePlan(plan);
            foreach (var old in plan.Originals.OrderBy(f => f.Name == "SonicRoute.exe" ? 1 : 0))
            {
                string target = Path.Combine(plan.TargetDirectory, plan.DestinationName(old.Name));
                UpdateProtocol.RejectReparsePoints(target);
                if (old.Exists)
                {
                    string backup = Path.Combine(plan.WorkDirectory, "backup", old.Name);
                    UpdateProtocol.Verify(backup, old.Sha256, old.Size);
                    if (File.Exists(target) && new FileInfo(target).Length == old.Size &&
                        UpdateProtocol.Hash(target).Equals(old.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                    string restore = Path.Combine(plan.WorkDirectory, "restore-" + old.Name);
                    if (File.Exists(restore)) File.Delete(restore);
                    DurableCopy(backup, restore);
                    if (File.Exists(target)) File.Replace(restore, target, null);
                    else File.Move(restore, target);
                    UpdateProtocol.Verify(target, old.Sha256, old.Size);
                }
                else if (File.Exists(target))
                {
                    var file = plan.Package.Files.Single(f => f.Path == old.Name);
                    UpdateProtocol.Verify(target, file.Sha256, file.Size);
                    File.Delete(target);
                }
            }
            plan.Status = "rolledBack"; WritePlan(plan);
            DeleteMarker(plan);
        }

        internal static void Commit(InstallPlan plan)
        {
            plan.Status = "committed"; WritePlan(plan);
            DeleteMarker(plan);
            try { SafeDeleteTree(plan.WorkDirectory); } catch { /* A locked backup can be removed on a later user-requested download. */ }
        }

        private static void DeleteMarker(InstallPlan p)
        {
            string marker = Path.Combine(p.TargetDirectory, MarkerName);
            if (File.Exists(marker) && File.ReadAllText(marker) == p.Id) File.Delete(marker);
        }

        internal static void SafeDeleteTree(string directory)
        {
            // No recursive OS delete that could follow junctions. Every entry is checked before descent.
            if (!Directory.Exists(directory)) return;
            UpdateProtocol.RejectReparsePoints(directory);
            foreach (string file in Directory.GetFiles(directory)) { UpdateProtocol.RejectReparsePoints(file); File.Delete(file); }
            foreach (string child in Directory.GetDirectories(directory)) SafeDeleteTree(child);
            Directory.Delete(directory);
        }
    }
}
