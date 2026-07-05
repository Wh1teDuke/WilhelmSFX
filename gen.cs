#!/usr/bin/env -S dotnet run

#:package SpessaSharp@4.3.12-nightly-00023
#:package YamlDotNet@18.0.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SpessaSharp.MIDI;
using SpessaSharp.SoundBank;
using SpessaSharp.SoundBank.SoundFont;
using SpessaSharp.Synthesizer.Engine.Voice;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
#pragma warning disable CS8321 // Local function is declared but never used
#pragma warning disable IL3050 // Using member 'X' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling.

// ----------------------------------------------------------------------------
// General Settings
const string bankName = "WilhelmSFX";
const string author = "WhiteDuke";
const string cache = ".cache";
const string samplesIn = "Samples";

SampleCache.Init(cache);
Log.Init(cache);
var sw = Stopwatch.StartNew();

// Read command line
var q = Math.Clamp(ArgsInt("-q") ?? 6, 0, 10);
var processReadme = !ArgsContains("--skip-readme");
var onlyReadme = ArgsContains("--only-readme");
var sampleRate = ArgsInt("--sample-rate") ?? 44_100;
var lowpass = ArgsInt("--lowpass");
var bitcrush = ArgsInt("--bitcrush");
var includeExtra = ArgsContains("--include-extra");
var filterOrigin = ArgsString("--filter-origin");
var deletedUnused = ArgsContains("--delete-unused");

var sourcesTxt = File.ReadAllText("sources.txt");

var settingsStr = $"SampleRate={sampleRate}, Q={q}, Lowpass={lowpass?.ToString() ?? "no"}, BitCrush={bitcrush?.ToString() ?? "no"}";
Console.WriteLine("[SETTINGS]");
Console.WriteLine(settingsStr);

Log.Line(settingsStr);

var fileToName = new Dictionary<string, string>();
var fileToCfg = new Dictionary<string, SampleCfg>();
var presetToCfg = new Dictionary<string, GroupCfg>();
var groupToCfg = new Dictionary<string, GroupCfg>();
var fileToUrl = new Dictionary<string, string>();

var outputDir = new DirectoryInfo(cache);
var inputDir = new DirectoryInfo(samplesIn);


// -----------------------------------------------------------------------------
// Read Sample Origin
foreach (var line in sourcesTxt
        .ReplaceLineEndings()
        .Split(Environment.NewLine))
{
    if (line.IsWhiteSpace() || line.StartsWith("//"))
        continue;

    var idx = line.TrimEnd().LastIndexOf(' ');
    if (idx == -1) Error($"Wrong source format: '{line}'");

    var name = line[..idx].Trim();
    var src = line[idx..].Trim();

    fileToUrl[name] = src;
}

// -----------------------------------------------------------------------------
// Read samples
var sampleList = new List<string>();

sampleList.AddRange(Directory.GetFiles(inputDir.FullName));
if (includeExtra && Directory.Exists("ExtraSamples"))
    sampleList.AddRange(Directory.GetFiles("ExtraSamples"));

// -----------------------------------------------------------------------------
// Read Config
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(PascalCaseNamingConvention.Instance)
    .WithNodeDeserializer(new SampleCfgDeserializer(), s => s.OnTop())
    .Build();

var presets = new Dictionary<string, List<(string, SampleCfg)>>();
var sampleLen = 0;

List<string> cfgFileList = ["config.yaml"];
if (includeExtra) cfgFileList.Add("config_extra.yaml");

Console.WriteLine();
Console.WriteLine("[PROCESS CONFIG FILES]");
var tempSampleList = sampleList[..];
foreach (var cfgFile in cfgFileList)
{
    if (!File.Exists(cfgFile)) continue;

    Console.WriteLine(">Reading cfg: " + cfgFile);
    var mConfig = deserializer.Deserialize<MConfig>(File.ReadAllText(cfgFile));
    var i = 0;
    
    foreach (var entry in mConfig.Presets)
    {
        Console.Write($"{entry.Key} ({entry.Value.Count}), ");
        if (i++ % 6 == 0) Console.WriteLine();
    
        var list = new List<(string, SampleCfg)>();
        foreach (var (name, value) in entry.Value)
        {
            var pIdx = tempSampleList.FindIndex(
                s => Path.GetFileNameWithoutExtension(s) == value.File);
            if (pIdx is -1) Error("Sample does not exist: " + value.File);
            
            value.Meta.Folder = Path.GetDirectoryName(tempSampleList[pIdx])!;
            value.Meta.Preset = entry.Key;
            value.Meta.Name = name;
            value.Meta.Ext = Path.GetExtension(tempSampleList[pIdx]);
            
            tempSampleList.RemoveAt(pIdx);

            if (fileToUrl.TryGetValue(value.File, out var url) &&
                SkipSample(url)) continue;

            fileToName[value.File] = name;
            fileToCfg[value.File] = value;
            
            list.Add((name, value));
            sampleLen++;
        }

        if (!presets.ContainsKey(entry.Key))
            presets[entry.Key] = [];
        presets[entry.Key].AddRange(list);
    }
    
    Console.WriteLine();

    if (mConfig.Settings is { } settings)
        foreach (var entry in settings)
            if (entry.Key.Equals("Presets"))
                foreach (var (preset, cfg) in entry.Value)
                    presetToCfg[preset] = cfg;
            else if (entry.Key.Equals("Groups"))
                foreach (var (preset, cfg) in entry.Value)
                    groupToCfg[preset] = cfg;
            else Error("Unknown settings key: " + entry.Key);
}

Console.WriteLine("Samples: " + sampleLen);
Console.WriteLine();

// -----------------------------------------------------------------------------
// Process Readme
if (processReadme)
{
    Console.WriteLine("[PROCESS README]");
    
    // Tags
    var tagList = new List<(string, HashSet<string>)>();
    
    foreach (var line in File
         .ReadAllText("tags.txt")
         .ReplaceLineEndings()
         .Split(Environment.NewLine))
    {
        if (line.IsWhiteSpace()) continue;
        if (line.Trim().StartsWith('#')) continue;
        
        var list = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = list[0].ToLowerInvariant();
        var tags = list[1..].Select(
            n => n.Trim().ToLowerInvariant()).ToHashSet();

        if (tags.Count == 0)
            Error("No tags found: " + name);
        
        var exists = presets.Values.Any(e => e.Any(e => 
            e.Item1[..^1].Equals(
                name, StringComparison.InvariantCultureIgnoreCase)));
        if (!exists) Warn($"No effect exists for tag '{name}'");
        
        tagList.Add((name, tags));
    }

    // Readme
    var readme =
        """
        # Wilhelm SFX

        ![Wilhelm stares at your soul](Sheb_Wooley_1971.jpeg "Wilhelm")

        A sound bank focused on sound effects.
        
        ## Build
        
        Requires `dotnet 10` and `ffmpeg`. Example:
        
        ```bash
        ./gen.cs --sample-rate 22_050
        ```
        
        Used sites:

        {0}

        ***

        ## Effect List ({1})
        
        """;

    var urls = new HashSet<string>();
    var count = 0;
    var firstGroup = true;
    var tagGroup = "-";
    var needTag = false;
    
    const string tagsVar = "!TAGS!";
    
    foreach (var line in sourcesTxt
                 .ReplaceLineEndings()
                 .Split(Environment.NewLine))
    {
        if (firstGroup && !line.StartsWith("//"))
            continue;
        firstGroup = false;
        
        if (line.IsWhiteSpace())
        {
            if (needTag) readme += tagsVar;
            readme += "\n\n";
            needTag = false;
            continue;
        }

        if (line.StartsWith("//"))
        {
            var catName = line[2..].Trim();
            readme += "&nbsp;\n### " +
                catName + $" ({presets[catName].Count})" + "\n\n";
            continue;
        }

        var idx = line.TrimEnd().LastIndexOf(' ');
        if (idx == -1) Error($"Wrong source format: '{line}'");

        var name = line[..idx].Trim();
        var src = line[idx..].Trim();

        if (SkipSample(src)) continue;

        urls.Add(new Uri(src).Host);
        
        var realName = fileToName[name];
        
        if (name.Contains("__"))
            name = name.Split("__")[^1];

        name = name.Replace("_", " ");
        name = name.Replace("-", " ");

        var nameMinusGroup = realName.Replace(
            tagGroup, null, StringComparison.InvariantCultureIgnoreCase);

        if (!nameMinusGroup.All(char.IsDigit))
        {
            needTag = true;
            var newTagGroup = realName[..^1].ToLowerInvariant();
            AddTags();
            
            tagGroup = newTagGroup;
            realName = realName[..^1] + " 1";
        }
        else realName = nameMinusGroup;

        readme += $"[{realName}]({src} \"{name}\") ";

        count++;
    }
    
    if (!readme.EndsWith(tagsVar))
        readme += tagsVar;

    AddTags();

    readme = string.Format(
        readme, 
        string.Join("\n", urls.Select(u => $"- [{u}](https://{u})")), 
        count);
    
    File.WriteAllText("README.md", readme);
    Console.WriteLine();

    if (onlyReadme) return;

    void AddTags()
    {
        if (tagGroup == "-") return;
        
        for (var i = 0; i < tagList.Count; i++)
        {
            var (tagKey, tagVal) = tagList[i];
            if (tagGroup != tagKey) continue;

            tagList.RemoveAt(i);

            var tags = string.Join(", ", tagVal.Select(n => $"`{n}`"));
            readme = readme.Replace(tagsVar, $"\n*{tags}*\n\n");
            return;
        }

        Error($"No tags found for '{tagGroup}'");
    }
}

// ----------------------------------------------------------------------------
// Process samples
if (!outputDir.Exists)
{
    outputDir.Refresh();
    outputDir.Create();   
}
{
    Console.WriteLine("[PROCESS SAMPLES]");

    const string argQuiet = 
        "-nostats -loglevel error -y -hide_banner ";
    const string argCompress = "-c:a libvorbis -ac 1 -ar {0} -q:a {1} ";

    const string filterTrim =
        "silenceremove=start_periods=1:start_threshold={0}dB:stop_periods=1:stop_threshold={0}dB:stop_duration=0.1,";
    const string filterVolNorm =
        "loudnorm=I=-14:TP=-1.0:LRA=6,";
    // TODO: Don't remove silence if sample is too short
    const string filterPlaybackRate = "atempo={0},";
    const string filterBitCrusher = "acrusher=bits={0}:mix=1:mode=log:samples=1,";
    const string filterLowPass = "lowpass=f={0}:p=2,";

    var processList = new List<Process>();
    var files = sampleList;

    var argList = new List<string>();
    
    foreach (var file in files)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var output = Path.Join(cache, $"{name}.ogg");

        if (!fileToCfg.TryGetValue(name, out var cfg))
        {
            if (deletedUnused)
            {
                Warn("Deleting unused sample: " + file);
                File.Delete(file);
            }
            else
                Warn("Unused sample: " + file);
            continue;
        }

        var pCfg = presetToCfg.GetValueOrDefault(cfg.Meta.Preset);
        var gCfg = GetGroupCfg(cfg.Meta.Name);

        var cmd =
            // Quiet output
            argQuiet +
            // Input
            $"-i \"{file}\" " +
            // Remove embedded pictures
            "-vn "
        ;

        var filters = "";

        // Trim silence from start/end
        if (cfg.Trim ?? true)
            filters += string.Format(filterTrim, cfg.TrimDb ?? -45);

        // Playback rate
        if (cfg.Speed is {} speed)
            filters += string.Format(filterPlaybackRate, speed);

        // Bit depth
        if (bitcrush is {} bc)
            filters += string.Format(filterBitCrusher, bc);

        // Lowpass
        if (lowpass is {} lp)
            filters += string.Format(filterLowPass, lp);

        // Normalize volume,
        if (cfg.Norm ?? true)
            filters += filterVolNorm;

        if (filters != "")
            filters = "-af \"" + filters.TrimEnd(',') + "\" ";

        var sRate = cfg.SampleRate ??
            gCfg?.SampleRate ?? pCfg?.SampleRate ?? sampleRate;
        var sQ = cfg.Q ??
            gCfg?.Q ?? pCfg?.Q ?? q;

        sRate = (int)Math.Round(sRate *
            (cfg.MulSampleRate ?? gCfg?.MulSampleRate ?? pCfg?.MulSampleRate ?? 1));
        sQ = (int)Math.Round(sQ *
            (cfg.MulQ ?? gCfg?.MulQ ?? pCfg?.MulQ ?? 1));

        // TODO: Save original samplerate
        sRate = int.Clamp(sRate, 750, 96_000);
        sQ = int.Clamp(sQ, 0, 10);

        cfg.Meta.FinalSampleRate = sRate;

        cmd +=
            // Filters
            filters +

            // Compress to vorbis (q = quality)
            string.Format(argCompress, sRate, sQ) +
            // Output
            $"\"{output}\""
        ;

        Log.Line($"[ffmpeg] {cmd}");

        if (SampleCache.Add(name, cmd)) argList.Add(cmd);
    }

    foreach (var cmdArgs in argList) 
        processList.Add(Process.Start("ffmpeg", cmdArgs));

    foreach (var process in processList)
        process.WaitForExit();

    Console.WriteLine();
}

// ----------------------------------------------------------------------------
// Get Duration
Console.WriteLine("[PROCESS SAMPLES DURATION]");

var sampleToDuration = new ConcurrentDictionary<string, double>();
var tasksDur = new List<Task>();
foreach (var sCfg in presets.Values
             .SelectMany(s => s.Select(ss => ss.Item2)))
{
    var sampleFile = sCfg.File;
    var sampleExt = sCfg.Meta.Ext;

    if (SampleCache.TryGetDuration(sampleFile) is { } duration)
    {
        WarnDuration(duration);
        sampleToDuration[sampleFile] = duration.New;
        continue;
    }

    var task = Task.Run(() =>
    {
        var pathOld = Path.Join(sCfg.Meta.Folder, sampleFile + sampleExt);
        var pathNew = Path.Join(outputDir.Name, sampleFile + ".ogg");
        
        var durOld = GetDuration(pathOld);
        var durNew = GetDuration(pathNew);

        WarnDuration((durOld, durNew));
        SampleCache.SetDuration(sampleFile, (durOld, durNew));
        return sampleToDuration[sampleFile] = durNew;
        
        double GetDuration(string file)
        {
            const string args = "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 ";
            return double.Parse(ProcessRead("ffprobe", args + file).Trim());
        }
    });

    tasksDur.Add(task);

    continue;

    void WarnDuration((double Old, double New) dur)
    {
        if (dur.New >= .15) return;
        if (dur.New / dur.Old > .51) return;
        
        Warn(
            $"'{sCfg.Meta.Preset}.{sCfg.Meta.Name}' excesive trim (Old={
            dur.Old:F3}, New={dur.New:F3})");
    }
}

Task.WaitAll(tasksDur.ToArray());


// ----------------------------------------------------------------------------
// We are cooking
Console.WriteLine();
Console.WriteLine("[BUILD SOUND BANK]");

var bank = new SoundBank();

var sep = new string('-', 40) + "\n";
var comment = $"A sound bank for SFX\nhttps://creativecommons.org/publicdomain/zero/1.0/deed.en\nSettings: {settingsStr}\n\n";
comment += sep;

var input = "// Start Effect List\n// Patch     - Key - Dur   -  Effect\n";
var p = -1;

foreach (var (presetName, sounds) in presets)
{
    if (sounds.Count > 128)
        Error($"Too many samples in preset '{presetName}'");

    p++;
    var patch = new MidiPatch(p % 128, 7 + p / 128, 7, false);
    
    // Instrument
    var instrument = new BasicInstrument();
    instrument.Name = "Ins_" + presetName;
    instrument.GlobalZone.SetGenerator(Generator.Type.SampleModes, 1);
    bank.Instruments.Add(instrument);

    var pCfg = presetToCfg.GetValueOrDefault(presetName);
    TrySetGenerators(instrument.GlobalZone, [pCfg]);
    
    var set = new HashSet<string>();

    var i = -1;
    foreach (var (soundName, cfg) in sounds)
    {
        i++;

        var fileName = cfg.File;


        if (fileToUrl.TryGetValue(fileName, out var url) &&
            SkipSample(url)) continue;

        var file = new FileInfo(Path.Join(cache, fileName + ".ogg"));
        if (!file.Exists) Error(
            "Sound preset file not found: " + file.FullName);

        // Sample
        var sRate = cfg.Meta.FinalSampleRate;
        var audioData = File.ReadAllBytes(file.FullName);
        var sampleName = presetName + "." + soundName;
        var sampleDuration = sampleToDuration[fileName];
        var sampleDurLen = (int)(sampleDuration * sRate);

        var gCfg = GetGroupCfg(sampleName);

        if (!set.Add(sampleName))
            Error($"Duplicated sample name: '{sampleName}'");
        
        var sample = BasicSample.NewEmpty();
        sample.Name = sampleName;
        sample.Rate = sRate;
        sample.OriginalKey = i;
        sample.SetCompressedData(audioData);
        
        if (cfg.LoopStart is {} lStart)
            sample.LoopStart = (int)(lStart * sRate);

        if (cfg.LoopEnd is { } lEnd)
        {
            int samples;

            if (lEnd == "all")
                samples = sampleDurLen;
            else if (double.TryParse(
                    lEnd, System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed))
                samples = (int)(parsed * sRate);
            else
                Error("Wrong sample loop arg: " + lEnd);

            sample.LoopEnd = samples;
        }

        var zone = instrument.CreateZone(sample);
        zone.Basic.KeyRange = (i, i);

        TrySetGenerators(zone.Basic, [cfg, gCfg]);

        if (sample.LoopEnd != 0 || sample.LoopStart != 0)
        {
            var loopModeSet =
                zone.Basic.GetGenerator(Generator.Type.SampleModes) != null;

            if ((
                cfg.ReleaseVolEnv ??
                cfg.ReleaseModEnv ??
                null) is not null)
            {
                // Empty
            }
            else if (sample.LoopEnd < sampleDurLen)
            {
                var sec = 
                    (sample.LoopEnd / (double)sampleDurLen) * sampleDuration;
                var tc = UnitConverter.SecondsToTimecents(sec);

                if (!loopModeSet)
                    zone.Basic.SetGenerator(Generator.Type.SampleModes, 3);
                zone.Basic.SetGenerator(Generator.Type.ReleaseVolEnv, tc);
            }
            else if (!loopModeSet)
                // TODO: if sample is very short, maybe mode 3 is a sensible default
                TrySetLoopMode(
                    zone.Basic, cfg.LoopMode?.ToLowerInvariant() ?? "1");
        }
        
        bank.Samples.Add(sample);

        input += $"{patch.BankLSB:000}:{patch.BankMSB:000}:{patch.Program:000}    {i:000}   {sampleDuration,-8:F3} {sampleName}\n";
    }

    input += "\n";

    // Preset
    var preset = new BasicPreset(bank);
    preset.Patch = new MidiPatch.Full(patch, presetName, false);
    /*discard*/preset.CreateZone(instrument);

    bank.Presets.Add(preset);
}

comment += input;
comment += "// End Effect List\n";

comment += sep;
comment += "Sources:\n" + sourcesTxt;

bank.Info = bank.Info with
{
    Copyright = "CC0 1.0 Universal",
    Engineer = author,
    Name = bankName,
    Comment = comment,
};

var sbFile = new FileInfo(bankName + ".sf3");
bank.WriteSF2(sbFile, SF2WriteOptions.Default with
{
    Compress = true,
    Software = "SpessaSynth",
});
sbFile.Refresh();

File.WriteAllText(bankName + ".cfg", input.TrimEnd());

var totalDuration = TimeSpan.FromSeconds(
    sampleToDuration.Sum(i => i.Value));

Util.Info(
    $"[SUCCESS({(int)sw.Elapsed.TotalSeconds}sec)]: {
    sbFile.Name}, {
    sbFile.Length / 1024d / 1024d:F1} MB, {
    totalDuration:mm\\:ss} total duration");
SampleCache.End();
return;

#region utils
bool ArgsContains(string arg) =>
    args.Contains(arg, StringComparer.OrdinalIgnoreCase);

int? ArgsIndex(string arg)
{
    var idx = args.IndexOf(arg, StringComparer.OrdinalIgnoreCase);
    return idx == -1 ? null : idx + 1;
}

int? ArgsInt(string arg) =>
    ArgsIndex(arg) is { } idx ? int.Parse(args[idx]) : null;

string? ArgsString(string arg) =>
    ArgsIndex(arg) is { } idx ? args[idx] : null;

string ProcessRead(string cmd, string args = "")
{
    using var proc = Process.Start(
        new ProcessStartInfo(cmd, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
    var reader = proc!.StandardOutput;
    var err = proc.StandardError;
    
    proc.WaitForExit();

    var errMsg = err.ReadToEnd();
    if (errMsg != "") Error(errMsg);
    
    return reader.ReadToEnd();
}

GroupCfg? GetGroupCfg(string sampleName)
{
    var groupName = sampleName.AsSpan();
    while (char.IsDigit(groupName[^1]))
        groupName = groupName[..^1];
    return groupToCfg.GetValueOrDefault(groupName.ToString());
}

bool SkipSample(string sampleOrigin) =>
    filterOrigin != null && !Regex.IsMatch(sampleOrigin, filterOrigin);

void TrySetFloat(BasicZone zone, Generator.Type gen, double? genVal)
{
    if (genVal is not {} val) return;
    zone.SetGenerator(gen, UnitConverter.SecondsToTimecents(val));
}

void TrySetInt(BasicZone zone, Generator.Type gen, int? genVal)
{
    if (genVal is not {} val) return;
    zone.SetGenerator(gen, val);
}

void TrySetLoopMode(BasicZone zone, string? genVal)
{
    if (genVal is not {} val) return;
    var mode = val.ToLowerInvariant() switch
    {
        "no" or "false" or "off" or "null" or "unset" => 0,
        "loop" => 1,
        "onrelease" => 2,
        "untilrelease" => 3,
        var m => int.Parse(m)
    };
    zone.SetGenerator(Generator.Type.SampleModes, mode);
}

void TrySetGenerators(BasicZone zone, List<BaseCfg?> cfgList)
{
    if (!cfgList.Any(cfg => cfg != null)) return;

    foreach (var cfg in cfgList)
        if (cfg?.SemitoneTuning is {} val)
        {
            TrySetInt(zone, Generator.Type.FineTune, val);
            break;
        }
    foreach (var cfg in cfgList)
        if (cfg?.AttackVolEnv is {} val)
        {
            TrySetFloat(zone, Generator.Type.AttackVolEnv, val);
            break;
        }
    foreach (var cfg in cfgList)
        if (cfg?.ReleaseVolEnv is {} val)
        {
            TrySetFloat(zone, Generator.Type.ReleaseVolEnv, val);
            break;
        }
    foreach (var cfg in cfgList)
        if (cfg?.AttackModEnv is {} val)
        {
            TrySetFloat(zone, Generator.Type.AttackModEnv, val);
            break;
        }
    foreach (var cfg in cfgList)
        if (cfg?.ReleaseModEnv is {} val)
        {
            TrySetFloat(zone, Generator.Type.ReleaseModEnv, val);
            break;
        }
    foreach (var cfg in cfgList)
        if (cfg?.ModEnvToPitch is {} val)
        {
            TrySetInt(zone, Generator.Type.ModEnvToPitch, val);
            break;
        }
    foreach (var cfg in cfgList)
        if (cfg?.LoopMode is {} val)
        {
            TrySetLoopMode(zone, val);
            break;
        }
}

[DoesNotReturn]
static void Error(string msg)
{
    Util.Error(msg);
    throw new Exception(); // Local variable 'X' might not be initialized before accessing
}

static void Warn(string msg) => Util.Warn(msg);

internal static class Util
{
    private static readonly Lock Lock = new ();
    
    [DoesNotReturn]
    public static void Error(string msg)
    {
        using var _ = Lock.EnterScope();

        if (Console.CursorLeft != 0)
            Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Red;
        msg = "[ERROR] " + msg;
        Log.Line(msg);
        Console.WriteLine(msg);
        Console.ResetColor();
        Environment.Exit(1);
        throw new Exception(); // Local variable 'X' might not be initialized before accessing
    }

    public static void Info(string msg)
    {
        using var _ = Lock.EnterScope();
        Console.ForegroundColor = ConsoleColor.Green;
        Log.Line("[INFO] " + msg);
        Console.WriteLine(msg);
        Console.ResetColor();
    }
    
    public static void Warn(string msg)
    {
        using var _ = Lock.EnterScope();

        if (Console.CursorLeft != 0)
            Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Yellow;
        msg = "[WARN] " + msg;
        Log.Line(msg);
        Console.WriteLine(msg);
        Console.ResetColor();
    }
}

internal static class SampleCache
{
    private const string CACHE_NAME = "0000_cache.txt";
    private const int VERSION = 1;
    
    private static readonly Dictionary<
        string,
        (string Args,
        (double Old, double New)? Duration)> Cache = [];

    private static readonly Lock Lock = new();

    private static string _cacheFile = "";

    public static bool Add(string filename, string args)
    {
        using var _ = Lock.EnterScope();

        var key = Hash(filename);
        var val = (
            Args: Hash(args), 
            Duration: ((double, double)?)null);

        if (!Cache.TryGetValue(key, out var value))
            return Cache.TryAdd(key, val);

        if (value.Args == val.Args) 
            return false;

        Cache[key] = val;
        return true;
    }

    public static (double Old, double New)? TryGetDuration(string filename)
    {
        using var _ = Lock.EnterScope();
        return Cache.TryGetValue(
            Hash(filename), out var val) ? val.Duration : null;
    }

    public static void SetDuration(string filename, (double, double) duration)
    {
        using var _ = Lock.EnterScope();
        ref var val = ref CollectionsMarshal.GetValueRefOrNullRef(
            Cache, Hash(filename));
        val.Duration = duration;
    }

    public static void Init(string folder)
    {
        using var _ = Lock.EnterScope();
        
        _cacheFile = Path.Join(folder, CACHE_NAME);
        if (!File.Exists(_cacheFile)) return;
        var lines = File.ReadAllLines(_cacheFile);

        if (int.Parse(lines[0]) != VERSION) return;

        try
        {
            foreach (var line in lines.AsSpan(1))
            {
                if (line.Split(' ', 4) is not [
                        var key, var args, var durOld, var durNew])
                    throw new Exception("Wrong cache line: " + line);
                Cache[key] = 
                    (args, (double.Parse(durOld), double.Parse(durNew)));
            }
        }
        catch (Exception e)
        {
            Util.Warn($"Error parsing {CACHE_NAME}: {e.Message}");
        }
    }

    public static void End()
    {
        using var _ = Lock.EnterScope();
        var sb = new StringBuilder();
        sb.AppendLine(VERSION.ToString());
        foreach (var (key, val) in Cache)
            sb.AppendLine(
                $"{key} {
                val.Args} {
                val.Duration!.Value.Old} {
                val.Duration!.Value.New}");

        File.WriteAllText(_cacheFile, sb.ToString().TrimEnd());
    }

    private static string Hash(string str) =>
        Convert.ToHexString(SHA1.HashData(
            Encoding.UTF8.GetBytes(str)));
}
#endregion

#region Log
internal static class Log
{
    private static readonly StringBuilder Sb = new ();
    private static readonly Lock Lock = new ();

    public static void Init(string cache)
    {
        var outputFile = Path.Join(cache, "0000_log.txt");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            File.WriteAllText(outputFile, Sb.ToString().TrimEnd());
    }

    public static void Line(string str)
    {
        using var _ = Lock.EnterScope();
        Sb.AppendLine(str);
    }
}
#endregion

#region Model
internal abstract class BaseCfg
{
    public double? Speed;
    public bool? Trim;
    public int? TrimDb;
    public bool? Norm;
    public int? Q;
    public int? SampleRate;

    public int? SemitoneTuning;

    public double? AttackVolEnv;
    public double? ReleaseVolEnv;

    public double? AttackModEnv;
    public double? ReleaseModEnv;

    public int? ModEnvToPitch;

    public string? LoopMode;

    public double? MulQ;
    public double? MulSampleRate;
}

internal sealed class GroupCfg: BaseCfg;

internal sealed class SampleCfg: BaseCfg
{
    public sealed class SampleMeta
    {
        public string Folder = "";
        public string Preset = "";
        public string Name = "";
        public string Ext = "";

        public int FinalSampleRate = 0;
    }

    public SampleMeta Meta = new();
    
    public string File;

    public double? LoopStart;
    public string? LoopEnd;
}

internal class MConfig
{
    public Dictionary<
        string, Dictionary<string, GroupCfg>>? Settings;

    public Dictionary<
        string, Dictionary<string, GroupCfg>>? Groups;

    public Dictionary<
        string, Dictionary<string, SampleCfg>> Presets;
}

internal sealed class SampleCfgDeserializer : INodeDeserializer
{
    public bool Deserialize(
        IParser parser,
        Type expectedType,
        Func<IParser, Type, object?> _,
        out object? value,
        ObjectDeserializer __)
    {
        if (expectedType == typeof(SampleCfg) &&
            parser.TryConsume<Scalar>(out var scalar))
        {
            value = new SampleCfg { File = scalar.Value };
            return true;
        }

        value = null;
        return false;
    }
}
#endregion