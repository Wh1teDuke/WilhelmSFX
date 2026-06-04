#!/usr/bin/env -S dotnet run --configuration Release

#:package SpessaSharp@4.3.7-nightly-00001
#:package YamlDotNet@18.0.0

using System.Collections.Concurrent;
using System.Diagnostics;

using SpessaSharp.MIDI;
using SpessaSharp.SoundBank;
using SpessaSharp.SoundBank.SoundFont;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// ReSharper disable CollectionNeverUpdated.Global
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value
#pragma warning disable CS8321 // Local function is declared but never used
#pragma warning disable IL3050 // Using member 'X' which has 'RequiresDynamicCodeAttribute' can break functionality when AOT compiling.

// General Settings
const string bankName = "WilhelmSFX";
const string author = "WhiteDuke";
const string samplesOutput = "SamplesOgg";
// Read command line
var q = Math.Clamp(ArgsInt("-q") ?? 2, -1, 10);
var processSamples = !ArgsContains("--skip-process");
var processReadme = !ArgsContains("--skip-readme");
var sampleRate = ArgsInt("--sample-rate") ?? 22_050;
var lowpass = ArgsInt("--lowpass") ?? sampleRate;
var bitcrush = ArgsInt("--bitcrush") ?? 32;

var sourcesTxt = File.ReadAllText("sources.txt");

var settingsStr = $"SampleRate={sampleRate}, Q={q}, Lowpass={lowpass}, BitCrush={bitcrush}";
Console.WriteLine(settingsStr);

// Read Config
var deserializer = new DeserializerBuilder()
    .WithNamingConvention(PascalCaseNamingConvention.Instance)
    .WithTypeConverter(new SampleCfgConverter())
    .Build();

var mConfig = deserializer.Deserialize<MConfig>(File.ReadAllText("config.yaml"));
var fileToName = new Dictionary<string, string>();
var presets = new List<(string Name, List<(string, SampleCfg)>)>();
var sampleLen = 0;

foreach (var entry in mConfig.Presets)
{
    sampleLen += entry.Value.Count;
    
    Console.WriteLine($"{entry.Key} ({entry.Value.Count})");
    
    var list = new List<(string, SampleCfg)>();
    foreach (var (name, value) in entry.Value)
    {
        fileToName[value.File] = name;
        list.Add((name, value));
    }

    presets.Add((entry.Key, list));
}

Console.WriteLine("Samples: " + sampleLen);

// Process Readme
if (processReadme)
{
    Console.WriteLine("Process Readme ...");
    var readme = 
        """
        # Wilhelm SFX

        ![Wilhelm stares at your soul](Sheb_Wooley_1971.jpeg "Wilhelm")

        A sound bank focused on sound effects.
        
        ## Compilation
        
        Requires `dotnet 10` and `ffmpeg`. Example:
        
        ```bash
        ./gen.cs --sample-rate 22_050
        ```
        
        Used sites:

        {0}

        ***

        ## Effect List
        
        """;

    var urls = new HashSet<string>();
    
    foreach (var line in sourcesTxt.Split(Environment.NewLine))
    {
        if (line.IsWhiteSpace())
        {
            readme += "&nbsp;\n&nbsp;\n\n";
            continue;
        }

        if (line.StartsWith("//"))
        {
            readme += "### " + line[2..].Trim() + "\n\n";
            continue;
        }

        if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not
            [var name, var src])
            throw new Exception($"Wrong source format: '{line}'");

        urls.Add(new Uri(src).Host);
        
        var realName = fileToName[name];
        
        if (name.Contains("__"))
            name = name.Split("__")[^1];

        name = name.Replace("_", " ");
        name = name.Replace("-", " ");

        readme += $"{realName} [{name}]({src})\n\n";
    }

    readme = string.Format(readme, string.Join("\n", urls.Select(u => $"- [{u}](https://{u})")));
    
    File.WriteAllText("README.md", readme);
}

// Process samples
var outputDir = new DirectoryInfo(samplesOutput);

if (processSamples)
{
    Console.WriteLine("Process Samples ...");
    
    if (outputDir.Exists) outputDir.Delete(true);
    outputDir.Refresh();
    outputDir.Create();

    var baseCmdArgs =
        // Quiet output
        "-nostats -loglevel quiet -y -hide_banner " +
        // Input
        "-i {0} " +

        // <filter>
        "-af \"" +
        // Bit depth
        $"acrusher=bits={bitcrush}:mix=1:mode=log:samples=2," +
        // Normalize,
        "loudnorm=I=-14:TP=-1.0:LRA=6," +
        // Lowpass
        $"lowpass=f={lowpass}," +
        // Trim silence from start/end
        "silenceremove=start_periods=1:start_threshold=-45dB:stop_periods=1:stop_threshold=-45dB:stop_duration=1" +
        // </filter>
        "\" " +

        // Compress to vorbis (q = quality)
        $"-c:a libvorbis -ac 1 -ar {sampleRate} -q:a {q} " +
        // Output
        "{1}";

    Console.WriteLine();
    Console.WriteLine(">ffmpeg " + baseCmdArgs, "[INPUT]", "[OUTPUT]");

    var processList = new List<Process>();
    foreach (var file in Directory.GetFiles("Samples"))
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var output = Path.Join(samplesOutput, $"{name}.ogg");
        var cmdArgs = string.Format(baseCmdArgs, file, output);
        processList.Add(Process.Start("ffmpeg", cmdArgs));
    }

    foreach (var process in processList)
        process.WaitForExit();

    Console.WriteLine();
}

// Get duration
Console.WriteLine("Process Samples Duration ...");

var sampleDur = new ConcurrentDictionary<string, double>();
var tasksDur = new List<Task>();
foreach (var sampleFile in presets
             .SelectMany(p => p.Item2)
             .Select(e => e.Item2.File))
{
    var path = Path.Join(outputDir.Name, sampleFile + ".ogg");

    var task = Task.Run(() =>
    {
        sampleDur[sampleFile] = double.Parse(ProcessRead(
            "ffprobe",
            $"-v quiet -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {path}").Trim());
    });
    tasksDur.Add(task);
}

Task.WaitAll(tasksDur.ToArray());

// We are cooking
Console.WriteLine("Process Sound Bank ...");

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
    
    var set = new HashSet<string>();

    var i = -1;
    foreach (var (soundName, sound) in sounds)
    {
        i++;

        var fileName = sound.File;
        var file = new FileInfo(Path.Join(samplesOutput, fileName + ".ogg"));
        if (!file.Exists) throw new FileNotFoundException(
            "Sound preset file not found", file.FullName);
     
        // Sample
        var audioData = File.ReadAllBytes(file.FullName);
        var sampleName = presetName + "." + soundName;
        var sampleDuration = sampleDur[fileName];

        if (!set.Add(sampleName))
            throw new ArgumentException($"Duplicated name: '{sampleName}'");
        
        var sample = BasicSample.NewEmpty();
        sample.Name = sampleName;
        sample.Rate = sampleRate;
        sample.OriginalKey = i;
        sample.SetCompressedData(audioData);
        sample.LoopStart = sound.LoopStart ?? 0;
        sample.LoopEnd = sound.LoopEnd ?? 0;

        var zone = instrument.CreateZone(sample);
        zone.Basic.KeyRange = (i, i);
        
        if (sample.LoopEnd != 0 || sample.LoopStart != 0)
            zone.Basic.SetGenerator(Generator.Type.SampleModes, 1);
        
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

bank.WriteSF2(new FileInfo(bankName + ".sf3"), SF2WriteOptions.Default with
{
    Compress = true,
    Software = "SpessaSynth",
});

File.WriteAllText(bankName + ".cfg", input.TrimEnd());

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
        });
    var reader = proc!.StandardOutput;
    proc.WaitForExit();
    return reader.ReadToEnd();
}
#endregion

#region Model
internal sealed class SampleCfg
{
    public string File = "";
    public int? LoopStart;
    public int? LoopEnd;
}

internal class MConfig
{
    public object? Settings;

    public Dictionary<
        string, Dictionary<string, SampleCfg>> Presets;
}

internal sealed class SampleCfgConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(SampleCfg);

    public object ReadYaml(
        IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
            return new SampleCfg { File = scalar.Value };

        parser.Consume<SequenceStart>();
        
        var cfg = new SampleCfg();
        cfg.File = parser.Consume<Scalar>().Value;
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            parser.Consume<MappingStart>();
            var key   = parser.Consume<Scalar>().Value;
            var value = int.Parse(parser.Consume<Scalar>().Value);
            parser.Consume<MappingEnd>();

            switch (key)
            {
                case "Start":
                    cfg.LoopStart = value;
                    break;
                case "End":
                    cfg.LoopEnd   = value;
                    break;
            }
        }

        return cfg;
    }

    public void WriteYaml(
        IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        => throw new NotImplementedException();
}
#endregion
