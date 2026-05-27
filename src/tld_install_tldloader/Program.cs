// One-shot TLDLoader installer.
//
// Replicates exactly what TLDworkshop.exe's PatchStarter does, but driven from a script so we
// don't need to run a Windows GUI app under Wine. Idempotent — safe to re-run on an already-
// installed game.
//
// Usage:
//   dotnet run -- <gameInstallDir> <protonAppId> [--m-ulti-tool <pathToMultiToolDir>]
//
// Example:
//   dotnet run -- "/home/reedo/.local/share/Steam/steamapps/common/The Long Drive Public" \
//                  2963147735 \
//                  --m-ulti-tool /home/reedo/Downloads/M-ultiTool_v4.0.1

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: <gameInstallDir> <protonAppId> [--m-ulti-tool <path>] [--skip-multitool]");
    return 2;
}

string gameDir = args[0];
string appId = args[1];

string? mtPath = null;
bool skipMT = false;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--m-ulti-tool" && i + 1 < args.Length) { mtPath = args[++i]; }
    else if (args[i] == "--skip-multitool") { skipMT = true; }
}
mtPath ??= "/home/reedo/Downloads/M-ultiTool_v4.0.1";

string managedDir = Path.Combine(gameDir, "TheLongDrive_Data", "Managed");
string asmPath = Path.Combine(managedDir, "Assembly-CSharp.dll");
string tldLoaderDest = Path.Combine(managedDir, "TLDLoader.dll");
string monoCecilDest = Path.Combine(managedDir, "Mono.Cecil.dll");
string harmonyDest = Path.Combine(managedDir, "0Harmony.dll");

string docsRoot = $"/home/reedo/.local/share/Steam/steamapps/compatdata/{appId}/pfx/drive_c/users/steamuser/Documents";
string tldDocs = Path.Combine(docsRoot, "TheLongDrive");
string modsFolder = Path.Combine(tldDocs, "Mods");
string assetsFolder = Path.Combine(modsFolder, "Assets");
string configFolder = Path.Combine(modsFolder, "Config", "Mod Settings");

// Source paths for files we drop in
string srcTLDLoader = "/tmp/tldloader/TLDLoader.dll";
string srcPatcherZip = "/tmp/tldpatcher/TLDPatcher.zip";
// 0Harmony / Mono.Cecil sourced from local BepInEx core dir (already on disk).
string srcHarmony = Path.Combine(gameDir, "BepInEx", "core", "0Harmony.dll");
string srcMonoCecil = Path.Combine(gameDir, "BepInEx", "core", "Mono.Cecil.dll");

void Info(string s) => Console.WriteLine($"[install] {s}");
void Warn(string s) => Console.WriteLine($"[install] WARN: {s}");

Info($"game = {gameDir}");
Info($"managed = {managedDir}");
Info($"appId = {appId}");
Info($"docs = {tldDocs}");

if (!File.Exists(asmPath)) { Console.Error.WriteLine($"missing {asmPath}"); return 3; }
if (!File.Exists(srcTLDLoader)) { Console.Error.WriteLine($"missing {srcTLDLoader} — fetch TLDLoader.dll first"); return 3; }
if (!File.Exists(srcPatcherZip)) { Console.Error.WriteLine($"missing {srcPatcherZip} — fetch TLDPatcher.zip first"); return 3; }

// ---- 1. backup + Cecil patch Assembly-CSharp.dll ----

string backup = asmPath + ".pre-tldloader.bak";
if (!File.Exists(backup))
{
    File.Copy(asmPath, backup);
    Info($"backed up Assembly-CSharp.dll -> {backup}");
}
else
{
    Info("backup already exists, not overwriting");
}

void PatchInjectCallAtStart(string targetType, string targetMethod, string callerType, string callerMethod)
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(managedDir);

    using var asm = ModuleDefinition.ReadModule(asmPath,
        new ReaderParameters { ReadWrite = true, AssemblyResolver = resolver });
    using var loader = ModuleDefinition.ReadModule(srcTLDLoader);

    var loaderTypeDef = loader.GetType(callerType);
    if (loaderTypeDef == null) throw new InvalidOperationException($"type not found in TLDLoader: {callerType}");
    var loaderMethodDef = loaderTypeDef.Methods.SingleOrDefault(m => m.Name == callerMethod);
    if (loaderMethodDef == null) throw new InvalidOperationException($"method not found: {callerType}.{callerMethod}");

    var asmTypeDef = asm.GetType(targetType);
    if (asmTypeDef == null) throw new InvalidOperationException($"type not found in Assembly-CSharp: {targetType}");
    var asmMethodDef = asmTypeDef.Methods.FirstOrDefault(m => m.Name == targetMethod);
    if (asmMethodDef == null) throw new InvalidOperationException($"method not found: {targetType}.{targetMethod}");

    // idempotency: if first instr is already a Call to our target, skip.
    var first = asmMethodDef.Body.Instructions.FirstOrDefault();
    if (first != null && first.OpCode == OpCodes.Call &&
        first.Operand is MethodReference mr && mr.Name == callerMethod &&
        mr.DeclaringType.FullName == callerType)
    {
        Info($"{targetType}.{targetMethod} already patched, skipping");
        return;
    }

    var imported = asm.ImportReference(loaderMethodDef);
    var call = Instruction.Create(OpCodes.Call, imported);
    asmMethodDef.Body.GetILProcessor().InsertBefore(asmMethodDef.Body.Instructions[0], call);
    asm.Write();
    Info($"patched: {targetType}.{targetMethod} -> Call {callerType}.{callerMethod}");
}

try
{
    PatchInjectCallAtStart("mainmenuscript", "Start", "TLDLoader.ModLoader", "InitMainMenu");
}
catch (Exception ex) { Warn($"mainmenuscript.Start patch: {ex.Message}"); }

try
{
    PatchInjectCallAtStart("itemdatabase", "Awake", "TLDLoader.ModLoader", "dbInit");
}
catch (Exception ex) { Warn($"itemdatabase.Awake patch: {ex.Message}"); }

// ---- 2. drop TLDLoader.dll + Cecil/Harmony into Managed ----

void CopyIfMissingOrNewer(string src, string dst)
{
    if (!File.Exists(src)) { Warn($"src missing: {src}"); return; }
    if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
    {
        File.Copy(src, dst, overwrite: true);
        Info($"copied {Path.GetFileName(src)} -> {dst}");
    }
    else
    {
        Info($"up-to-date: {dst}");
    }
}

CopyIfMissingOrNewer(srcTLDLoader, tldLoaderDest);
CopyIfMissingOrNewer(srcMonoCecil, monoCecilDest);
CopyIfMissingOrNewer(srcHarmony, harmonyDest);

// ---- 3. extract TLDPatcher.zip to Documents/TheLongDrive/Mods/Assets ----

Directory.CreateDirectory(assetsFolder);
using (var zip = ZipFile.OpenRead(srcPatcherZip))
{
    foreach (var entry in zip.Entries)
    {
        var target = Path.Combine(assetsFolder, entry.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (string.IsNullOrEmpty(entry.Name)) continue; // a directory entry
        if (File.Exists(target))
        {
            Info($"  asset exists: {entry.FullName}");
            continue;
        }
        entry.ExtractToFile(target);
        Info($"  extracted: {entry.FullName} -> Assets/");
    }
}

// ---- 4. set up Mods folder for user mods ----

Directory.CreateDirectory(modsFolder);
Directory.CreateDirectory(configFolder);
Info($"Mods folder ready: {modsFolder}");

// ---- 5. drop M-ultiTool ----

if (!skipMT)
{
    string mtDll = Path.Combine(mtPath, "M-ultiTool.dll");
    string mtCfg = Path.Combine(mtPath, "Config");
    if (File.Exists(mtDll))
    {
        File.Copy(mtDll, Path.Combine(modsFolder, "M-ultiTool.dll"), overwrite: true);
        Info($"copied M-ultiTool.dll");
    }
    else
    {
        Warn($"M-ultiTool.dll not found at {mtDll}");
    }
    if (Directory.Exists(mtCfg))
    {
        // Recursive copy Config -> Mods/Config (so the per-mod settings are preserved)
        void CopyDir(string s, string d)
        {
            Directory.CreateDirectory(d);
            foreach (var f in Directory.GetFiles(s))
                File.Copy(f, Path.Combine(d, Path.GetFileName(f)), overwrite: true);
            foreach (var sub in Directory.GetDirectories(s))
                CopyDir(sub, Path.Combine(d, Path.GetFileName(sub)));
        }
        CopyDir(mtCfg, Path.Combine(modsFolder, "Config"));
        Info($"copied M-ultiTool Config");
    }
}

Info("done.");
return 0;
