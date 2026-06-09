#!/usr/bin/env -S dotnet run

#:package SpessaSharp@4.3.8-nightly-00004
#:package YamlDotNet@18.0.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SpessaSharp.MIDI;
using SpessaSharp.SoundBank;
using SpessaSharp.SoundBank.SoundFont;

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
const string samplesOutput = "SamplesOgg";

var sw = Stopwatch.StartNew();

// Read command line
var q = Math.Clamp(ArgsInt("-q") ?? 2, -1, 10);
var processSamples = !ArgsContains("--skip-process");
var processReadme = !ArgsContains("--skip-readme");
var onlyReadme = ArgsContains("--only-readme");
var sampleRate = ArgsInt("--sample-rate") ?? 22_050;
var lowpass = ArgsInt("--lowpass") ?? sampleRate;
var bitcrush = ArgsInt("--bitcrush") ?? 32;
var saveDuration = ArgsContains("--save-duration");
var includeExtra = ArgsContains("--include-extra");

var sourcesTxt = File.ReadAllText("sources.txt");

var settingsStr = $"SampleRate={sampleRate}, Q={q}, Lowpass={lowpass}, BitCrush={bitcrush}";
Console.WriteLine("[SETTINGS]");
Console.WriteLine(settingsStr);


// ----------------------------------------------------------------------------
// Read Config
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(PascalCaseNamingConvention.Instance)
    .WithTypeConverter(new SampleCfgConverter())
    .Build();

var fileToName = new Dictionary<string, string>();
var fileToCfg = new Dictionary<string, SampleCfg>();
var presetToCfg = new Dictionary<string, GroupCfg>();

var presets = new Dictionary<string, List<(string, SampleCfg)>>();
var sampleLen = 0;

List<string> cfgFileList = ["config.yaml"];
if (includeExtra) cfgFileList.Add("config_extra.yaml");

Console.WriteLine();
Console.WriteLine("[PROCESS CONFIG FILES]");
foreach (var cfgFile in cfgFileList)
{
    if (!File.Exists(cfgFile)) continue;

    Console.WriteLine(">Reading cfg: " + cfgFile);
    var mConfig = deserializer.Deserialize<MConfig>(File.ReadAllText(cfgFile));
    var i = 0;
    
    foreach (var entry in mConfig.Presets)
    {
        sampleLen += entry.Value.Count;
    
        Console.Write($"{entry.Key} ({entry.Value.Count}), ");
        if (i++ % 6 == 0) Console.WriteLine();
    
        var list = new List<(string, SampleCfg)>();
        foreach (var (name, value) in entry.Value)
        {
            fileToName[value.File] = name;
            fileToCfg[value.File] = value;
            
            list.Add((name, value));
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
            else Error("Unknown settings key: " + entry.Key);
}

Console.WriteLine("Samples: " + sampleLen);
Console.WriteLine();

// ----------------------------------------------------------------------------
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
            e.Item1[..^1].Equals(name, StringComparison.InvariantCultureIgnoreCase)));
        if (!exists) Error($"No effect exists for tag '{name}'");
        
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
            readme += "&nbsp;\n### " + line[2..].Trim() + "\n\n";
            continue;
        }

        if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not
            [var name, var src])
            Error($"Wrong source format: '{line}'");

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
            readme = readme.Replace(tagsVar, $"\n\n*{tags}*\n\n");
            return;
        }

        Error($"No tags found for '{tagGroup}'");
    }
}

// ----------------------------------------------------------------------------
// Process samples
var outputDir = new DirectoryInfo(samplesOutput);

if (processSamples)
{
    Console.WriteLine("[PROCESS SAMPLES]");
    
    if (outputDir.Exists) outputDir.Delete(true);
    outputDir.Refresh();
    outputDir.Create();

    const string argFilterTrim =
        "silenceremove=start_periods=1:start_threshold=-40dB:stop_periods=1:stop_threshold=-40dB:stop_duration=0.1,";
    const string argQuiet = 
        "-nostats -loglevel error -y -hide_banner ";
    const string argVolNorm = 
        "loudnorm=I=-14:TP=-1.0:LRA=6,";

    var baseCmdArgs =
        // Quiet output
       argQuiet +
        // Input
        "-i {0} " +
        // Remove embedded pictures
        "-vn " +

        // <filter>
        "-af \"" +
        // Trim silence from start/end
        argFilterTrim +
        // Playback rate
        "atempo={2}," +
        // Bit depth
        $"acrusher=bits={bitcrush}:mix=1:mode=log:samples=1," +
        // Lowpass
        $"lowpass=f={lowpass}:p=2," +
        // Normalize volume,
        argVolNorm +
        // </filter>
        "\" " +

        // Compress to vorbis (q = quality)
        $"-c:a libvorbis -ac 1 -ar {sampleRate} -q:a {q} " +
        // Output
        "{1}";

    Console.WriteLine(
        ">ffmpeg " + baseCmdArgs, 
        "[INPUT]", 
        "[OUTPUT]", 
        "[x]");

    var processList = new List<Process>();
    var files = Directory.GetFiles("Samples").ToList();
    if (includeExtra && Directory.Exists("ExtraSamples"))
        files.AddRange(Directory.GetFiles("ExtraSamples"));

    var argList = new List<string>();
    
    foreach (var file in files)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var output = Path.Join(samplesOutput, $"{name}.ogg");

        if (!fileToCfg.ContainsKey(name))
        {
            Warn("Unused sample: " + file);
            continue;
        }

        var cfg = fileToCfg[name];
        var playbackRate = cfg.Speed ?? 1;

        var cmdArgs = string.Format(
            baseCmdArgs,
            file,
            output,
            playbackRate);

        if (cfg.SkipTrim is true)
            cmdArgs = cmdArgs.Replace(argFilterTrim, null);
        
        if (cfg.SkipNorm is true)
            cmdArgs = cmdArgs.Replace(argVolNorm, null);

        argList.Add(cmdArgs);
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
foreach (var sampleFile in presets.Values
             .SelectMany(s => s.Select(ss => ss.Item2.File)))
{
    var path = Path.Join(outputDir.Name, sampleFile + ".ogg");
    var task = Task.Run(() => sampleToDuration[sampleFile] = double.Parse(ProcessRead(
        "ffprobe",
        $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {path}").Trim()));
    tasksDur.Add(task);
}

Task.WaitAll(tasksDur.ToArray());

if (saveDuration)
{
    var durTxt = "";
    foreach (var entry in sampleToDuration) 
        durTxt += $"{entry.Value,-8:F4} {entry.Key}\n";

    File.WriteAllText("duration.txt", durTxt);   
}

// ----------------------------------------------------------------------------
// We are cooking
Console.WriteLine();
Console.WriteLine("[BUILD SOUND BANK]");

var bank = new SoundBank();

var sep = new string('-', 40) + "\n";
var comment = $"A sound bank for SFX\nhttps://creativecommons.org/publicdomain/zero/1.0/deed.en\nSettings: {settingsStr}\n\n";
comment += sep;

var input = "// Start Effect List\n// Patch - Key - Dur   -  Effect\n";
var p = -1;

foreach (var (presetName, sounds) in presets)
{
    p++;
    var patch = new MidiPatch(p % 128, 7 + p / 128, 0, false);
    
    // Instrument
    var instrument = new BasicInstrument();
    instrument.Name = "Ins_" + presetName;
    instrument.GlobalZone.SetGenerator(Generator.Type.SampleModes, 1);
    bank.Instruments.Add(instrument);

    var pCfg = presetToCfg.GetValueOrDefault(presetName);
    var set = new HashSet<string>();

    var i = -1;
    foreach (var (soundName, sound) in sounds)
    {
        i++;

        var fileName = sound.File;
        var file = new FileInfo(Path.Join(samplesOutput, fileName + ".ogg"));
        if (!file.Exists) Error(
            "Sound preset file not found: " + file.FullName);
     
        // Sample
        var audioData = File.ReadAllBytes(file.FullName);
        var sampleName = presetName + "." + soundName;
        var sampleDuration = sampleToDuration[fileName];
        var sampleDurLen = (int)(sampleDuration * sampleRate);

        if (!set.Add(sampleName))
            Error($"Duplicated sample name: '{sampleName}'");
        
        var sample = BasicSample.NewEmpty();
        sample.Name = sampleName;
        sample.Rate = sampleRate;
        sample.OriginalKey = i;
        sample.SetCompressedData(audioData);
        
        if (sound.LoopStart is {} lStart)
            sample.LoopStart = (int)(lStart * sampleRate);

        if (sound.LoopEnd is { } lEnd)
        {
            int samples;

            if (lEnd is string end)
            {
                if (end == "all")
                    samples = sampleDurLen;
                else if (end.EndsWith('%'))
                    samples = (int)(sampleDuration * sampleRate * double.Parse(end[..^2]));
                else
                    Error("Wrong sample loop arg: " + lEnd);
            }
            else
                samples = (int)((double)lEnd * sampleRate);

            sample.LoopEnd = samples;
        }

        var zone = instrument.CreateZone(sample);
        zone.Basic.KeyRange = (i, i);
        
        if (pCfg != null)// TODO: Add this to intrument global zone
        {
            if (pCfg.ReleaseVolEnv is {} rve)
                zone.Basic.SetGenerator(
                    Generator.Type.ReleaseVolEnv, (int)(rve * 1_000));
            if (pCfg.ReleaseModEnv is {} rme)
                zone.Basic.SetGenerator(
                    Generator.Type.ReleaseModEnv, (int)(rme * 1_000));
        }

        if (sample.LoopEnd != 0 || sample.LoopStart != 0)
        {
			var mode = sound.LoopMode ?? 1;// TODO: if sample is very short, maybe mode 3 is a sensible default

            if (sample.LoopEnd < sampleDurLen)
            {
                var ms = (int)
                    ((sample.LoopEnd / (double)sampleDurLen) *
                     sampleDuration * 1_000) * 5;

                zone.Basic.SetGenerator(Generator.Type.SampleModes, 3);
                zone.Basic.SetGenerator(Generator.Type.ReleaseVolEnv, ms);
                zone.Basic.SetGenerator(Generator.Type.ReleaseModEnv, ms);
            }
            else
                zone.Basic.SetGenerator(Generator.Type.SampleModes, mode);
        }
        
        bank.Samples.Add(sample);

        input += $"{patch.BankMSB:000}:{patch.Program:000}    {i:000}   {sampleDuration,-8:F3} {sampleName}\n";
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

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine(
    $"[SUCCESS({(int)sw.Elapsed.TotalSeconds}sec)]: {
    sbFile.Name}, {
    sbFile.Length / 1024d / 1024d:F1} MB, {
    totalDuration:mm\\:ss} samples duration");
Console.ResetColor();

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

[DoesNotReturn]
static void Error(string msg)
{
    Util.Error(msg);
    throw new Exception(); // Local variable 'X' might not be initialized before accessing
}

static void Warn(string msg)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("[WARN] " + msg);
    Console.ResetColor();
}

internal static class Util
{
    [DoesNotReturn]
    public static void Error(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("[ERROR] " + msg);
        Console.ResetColor();
        Environment.Exit(1);
        throw new Exception(); // Local variable 'X' might not be initialized before accessing
    }
}
#endregion

#region Model
internal sealed class GroupCfg
{
    public double? ReleaseVolEnv;
    public double? ReleaseModEnv;
}

internal sealed class SampleCfg
{
    public string File = "";
    public double? LoopStart;
    public object? LoopEnd;
	public int? LoopMode;

    public double? Speed;
    public bool? SkipTrim;
    public bool? SkipNorm;
}

internal class MConfig
{
    public Dictionary<
        string, Dictionary<string, GroupCfg>>? Settings;

    public Dictionary<
        string, Dictionary<string, SampleCfg>> Presets;
}

internal sealed class SampleCfgConverter : IYamlTypeConverter
{
    private Mark _prev;
    
    public bool Accepts(Type type) => type == typeof(SampleCfg);

    public object ReadYaml(IParser parser, Type __, ObjectDeserializer ___)
    {
        if (TryConsume<Scalar>(out var scalar))
            return new SampleCfg { File = scalar.Value };

        Consume<SequenceStart>();
        
        var cfg = new SampleCfg { File = Consume<Scalar>().Value };
        while (!TryConsume<SequenceEnd>(out _))
        {
            Consume<MappingStart>();
            switch (Consume<Scalar>().Value)
            {
                case "SkipTrim":
                    cfg.SkipTrim = GetBool();
                    break;
                case "SkipNorm":
                    cfg.SkipNorm = GetBool();
                    break;
                case "Start":
                    cfg.LoopStart = GetDouble();
                    break;
                case "End":
                    var val = Consume<Scalar>().Value;
                    if (val.Equals("all", StringComparison.CurrentCultureIgnoreCase))
                        cfg.LoopEnd = "all";
                    else
                        cfg.LoopEnd = double.Parse(val);
                    break;
				case "LoopMode":
					switch (Consume<Scalar>().Value.ToLowerInvariant())
					{
						case "untilrelease":
							cfg.LoopMode = 3;
							break;
						case var m:
                            Error("Unknown loop mode: " + m);
                            break;
						}
					break;
                case "Speed":
                    cfg.Speed = GetDouble();
                    break;
            }
            Consume<MappingEnd>();
        }

        return cfg;

        int GetInt() => int.Parse(Consume<Scalar>().Value);
        bool GetBool() => bool.Parse(Consume<Scalar>().Value);
        double GetDouble() => double.Parse(Consume<Scalar>().Value);

        T Consume<T>() where T : ParsingEvent
        {
            _prev = parser.Current?.Start ?? new();
            return parser.Consume<T>();
        }

        bool TryConsume<T>([MaybeNullWhen(false)] out T @event) where T : ParsingEvent
        {
            _prev = parser.Current?.Start ?? new();
            return parser.TryConsume(out @event);
        }
        
        void Error(string msg) => 
            Util.Error($"[Line: {_prev.Line}, Col: {_prev.Column}] " + msg);
    }

    public void WriteYaml(
        IEmitter _, object? __, Type ___, ObjectSerializer ____)
        => throw new NotImplementedException();
}
#endregion