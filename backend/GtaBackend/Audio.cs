using System.Text;
using System.Xml;
using System.Xml.Linq;
using CodeWalker.GameFiles;

namespace GtaBackend;

/// <summary>
/// Motorsounds: audioNameHash in vehicles.meta (GTA-Standardsound oder eigener Sound-Pack),
/// eigene Sound-Packs (audio\&lt;quelle&gt;\sfx\&lt;pack&gt;\*.awc + audio\&lt;quelle&gt;\audioconfig\*.rel)
/// anzeigen, anhören (WAV), exportieren, ersetzen, Einstellungen (.rel als XML) bearbeiten.
/// </summary>
public static class Audio
{
    // ------------------------------------------------------------------ Übersicht

    public static object Info(string sessionDir)
    {
        var warnings = new List<string>();
        var md = MetaData.Read(sessionDir, warnings);
        var audioDir = Path.Combine(sessionDir, "audio");
        var packs = new List<object>();
        var soundNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Name -> Pack

        // Kandidaten zum Auflösen von Hash-Namen
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in md.Vehicles)
        {
            candidates.Add(v.Model);
            if (!string.IsNullOrEmpty(v.AudioName)) candidates.Add(v.AudioName);
            if (!string.IsNullOrEmpty(v.GameName)) candidates.Add(v.GameName);
        }

        if (Directory.Exists(audioDir))
        {
            // Bekannte Stream-Namen registrieren, damit AWC-Spuren lesbare Namen bekommen
            foreach (var srcDir in Directory.GetDirectories(audioDir))
            {
                var sfx0 = Path.Combine(srcDir, "sfx");
                if (!Directory.Exists(sfx0)) continue;
                foreach (var pack in Directory.GetDirectories(sfx0))
                {
                    var pn = Path.GetFileName(pack).Replace("dlc_", "");
                    foreach (var awc in Directory.GetFiles(pack, "*.awc"))
                    {
                        var an = Path.GetFileNameWithoutExtension(awc);
                        foreach (var s in StreamNameGuesses)
                        {
                            JenkIndex.Ensure(s);
                            JenkIndex.Ensure($"{pn}_{s}");
                            JenkIndex.Ensure($"{an}_{s}");
                        }
                    }
                }
            }
            foreach (var srcDir in Directory.GetDirectories(audioDir))
            {
                var cfgDir = Path.Combine(srcDir, "audioconfig");
                var sfxDir = Path.Combine(srcDir, "sfx");
                // Namen aus .nametable und Pack-Ordnern als Kandidaten
                if (Directory.Exists(cfgDir))
                    foreach (var nt in Directory.GetFiles(cfgDir, "*.nametable"))
                        foreach (var n in Encoding.ASCII.GetString(File.ReadAllBytes(nt)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                            candidates.Add(n.Trim());
                if (Directory.Exists(sfxDir))
                    foreach (var d in Directory.GetDirectories(sfxDir)) candidates.Add(Path.GetFileName(d).Replace("dlc_", ""));

                var rels = new List<object>();
                if (Directory.Exists(cfgDir))
                {
                    foreach (var f in Directory.GetFiles(cfgDir).OrderBy(x => x))
                    {
                        if (f.EndsWith(".nametable", StringComparison.OrdinalIgnoreCase)) continue;
                        var info = new Dictionary<string, object>
                        {
                            ["file"] = Rel(sessionDir, f),
                            ["name"] = Path.GetFileName(f),
                            ["kind"] = RelKindLabel(f),
                        };
                        try
                        {
                            var rel = LoadRel(f);
                            var cars = rel.RelDatas?.OfType<Dat151CarAudioSettings>().Select(c => ResolveName(c, candidates)).ToList() ?? new List<string>();
                            info["cars"] = cars;
                            info["items"] = rel.RelDatas?.Length ?? 0;
                            foreach (var c in cars) soundNames.TryAdd(c, Path.GetFileName(srcDir));
                        }
                        catch (Exception ex) { info["error"] = ex.Message; }
                        rels.Add(info);
                    }
                }

                var waves = new List<object>();
                if (Directory.Exists(sfxDir))
                {
                    foreach (var pack in Directory.GetDirectories(sfxDir))
                    {
                        foreach (var awcFile in Directory.GetFiles(pack, "*.awc").OrderBy(x => x))
                        {
                            var awcInfo = new Dictionary<string, object>
                            {
                                ["file"] = Rel(sessionDir, awcFile),
                                ["pack"] = Path.GetFileName(pack),
                                ["name"] = Path.GetFileName(awcFile),
                                ["size"] = new FileInfo(awcFile).Length,
                            };
                            try { awcInfo["streams"] = StreamList(LoadAwc(awcFile)); }
                            catch (Exception ex) { awcInfo["error"] = ex.Message; }
                            waves.Add(awcInfo);
                        }
                    }
                }
                packs.Add(new { source = Path.GetFileName(srcDir), rels, waves });
            }
        }

        var vehicles = md.Vehicles.Select(v => new
        {
            model = v.Model,
            audioName = v.AudioName,
            custom = !string.IsNullOrEmpty(v.AudioName) && soundNames.ContainsKey(v.AudioName),
            customPack = !string.IsNullOrEmpty(v.AudioName) && soundNames.TryGetValue(v.AudioName, out var p) ? p : null,
        }).ToList();

        return new { vehicles, packs, customSounds = soundNames.Keys.OrderBy(x => x).ToList(), warnings };
    }

    static readonly string[] StreamNameGuesses =
    {
        "engine_accel", "engine_decel", "exhaust_accel", "exhaust_decel", "engine_idle", "exhaust_idle",
        "engine_accel_loop", "exhaust_accel_loop", "engine", "exhaust", "idle", "accel", "decel", "startup", "start",
        "shutdown", "stop", "revs", "revs_off", "rev", "limiter", "backfire", "pops", "turbo", "turbo_whine", "blowoff",
        "dump_valve", "transmission", "gear", "gearchange", "horn", "electric", "electric_engine", "boost", "whine",
        "left", "right", "loop", "low", "mid", "high",
    };

    static string ResolveName(RelData d, HashSet<string> candidates)
    {
        if (!string.IsNullOrEmpty(d.Name)) return d.Name;
        uint h = d.NameHash.Hash;
        foreach (var c in candidates) if (JenkHash.GenHash(c.ToLowerInvariant()) == h) return c;
        var s = d.NameHash.ToString();
        return string.IsNullOrEmpty(s) ? $"0x{h:X8}" : s;
    }

    static string RelKindLabel(string f)
    {
        var n = Path.GetFileName(f).ToLowerInvariant();
        if (n.Contains(".dat151")) return "Sound-Einstellungen (game)";
        if (n.Contains(".dat54")) return "Sound-Daten (sounds)";
        if (n.Contains(".dat10")) return "Synthesizer (amp)";
        if (n.Contains(".dat15")) return "Mixer (mix)";
        if (n.Contains(".dat4")) return "Sprache/Speech";
        if (n.Contains(".dat16")) return "Kurven (curves)";
        if (n.Contains(".dat22")) return "Kategorien";
        return "Audio-Daten";
    }

    static string Rel(string sessionDir, string f) => Path.GetRelativePath(sessionDir, f).Replace('\\', '/');

    static AwcFile LoadAwc(string file)
    {
        var awc = new AwcFile();
        var entry = new RpfBinaryFileEntry { Name = Path.GetFileName(file), NameLower = Path.GetFileName(file).ToLowerInvariant() };
        awc.Load(File.ReadAllBytes(file), entry);
        awc.Name = Path.GetFileNameWithoutExtension(file);
        return awc;
    }

    static RelFile LoadRel(string file)
    {
        var rel = new RelFile();
        var entry = new RpfBinaryFileEntry { Name = Path.GetFileName(file), NameLower = Path.GetFileName(file).ToLowerInvariant() };
        rel.Load(File.ReadAllBytes(file), entry);
        return rel;
    }

    static List<object> StreamList(AwcFile awc)
    {
        var list = new List<object>();
        var streams = awc.Streams ?? Array.Empty<AwcStream>();
        for (int i = 0; i < streams.Length; i++)
        {
            var s = streams[i];
            if (s == null || s.Hash == 0) continue; // Mehrkanal-Quelle
            list.Add(new
            {
                index = i,
                name = s.Name,
                seconds = Math.Round(s.Length, 2),
                sampleRate = s.SamplesPerSecond,
                granular = s.GranularGrainsChunk != null,
                midi = s.MidiChunk != null,
            });
        }
        return list;
    }

    // ------------------------------------------------------------------ vehicles.meta: audioNameHash

    public static object SetAudioName(string sessionDir, string model, string audioName, string backupDir)
    {
        audioName = (audioName ?? "").Trim();
        if (audioName.Length == 0) throw new UserError("Bitte einen Sound-Namen angeben.");
        var dataDir = Path.Combine(sessionDir, "data");
        if (!Directory.Exists(dataDir)) throw new UserError("Keine vehicles.meta vorhanden (Replace-Fahrzeug). Der Motorsound kommt dann vom Original-Fahrzeug.");
        foreach (var f in Directory.GetFiles(dataDir, "*.meta", SearchOption.AllDirectories))
        {
            XDocument doc;
            try { doc = MetaData.LoadXml(f); } catch { continue; }
            if (doc.Root?.Name.LocalName != "CVehicleModelInfo__InitDataList") continue;
            var item = doc.Root.Descendants().Where(e => e.Name.LocalName == "Item")
                .FirstOrDefault(e => e.Elements().Any(c => c.Name.LocalName == "modelName" && c.Value.Trim().Equals(model, StringComparison.OrdinalIgnoreCase)));
            if (item == null) continue;
            var el = item.Elements().FirstOrDefault(c => c.Name.LocalName == "audioNameHash");
            if (el == null)
            {
                el = new XElement("audioNameHash");
                var after = item.Elements().FirstOrDefault(c => c.Name.LocalName == "handlingId") ?? item.Elements().First();
                after.AddAfterSelf(el);
            }
            el.Value = audioName;
            Session.Backup(f, backupDir);
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false) };
            using (var w = XmlWriter.Create(f, settings)) doc.Save(w);
            return new { file = Rel(sessionDir, f), audioName };
        }
        throw new UserError($"{model} wurde in keiner vehicles.meta gefunden.");
    }

    // ------------------------------------------------------------------ WAV: anhören / exportieren

    static AwcStream GetStream(AwcFile awc, int index)
    {
        var s = awc.Streams;
        if (s == null || index < 0 || index >= s.Length || s[index] == null) throw new UserError("Sound nicht gefunden.");
        return s[index];
    }

    public static object StreamWav(string awcFile, int index, string outFile)
    {
        var awc = LoadAwc(awcFile);
        var st = GetStream(awc, index);
        var wav = st.GetWavFile();
        if (wav == null) throw new UserError("Dieser Sound kann nicht als WAV ausgegeben werden.");
        if (!string.IsNullOrEmpty(outFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllBytes(outFile, wav);
            return new { file = outFile, name = st.Name };
        }
        return new { name = st.Name, wav = Convert.ToBase64String(wav) };
    }

    public static object ExportAll(string awcFile, string outDir)
    {
        var awc = LoadAwc(awcFile);
        Directory.CreateDirectory(outDir);
        var files = new List<string>();
        var streams = awc.Streams ?? Array.Empty<AwcStream>();
        for (int i = 0; i < streams.Length; i++)
        {
            var s = streams[i];
            if (s == null || s.Hash == 0 || s.MidiChunk != null) continue;
            try
            {
                var wav = s.GetWavFile();
                if (wav == null) continue;
                var f = Path.Combine(outDir, Textures.Safe(s.Name) + ".wav");
                File.WriteAllBytes(f, wav);
                files.Add(f);
            }
            catch (Exception ex) { Program.Log($"WAV {s.Name}: {ex.Message}"); }
        }
        return new { files, outDir };
    }

    // ------------------------------------------------------------------ WAV ersetzen

    public static object ReplaceStream(string awcFile, int index, string wavFile, string backupDir)
    {
        if (!File.Exists(wavFile)) throw new UserError("Datei nicht gefunden.");
        if (!wavFile.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            throw new UserError("Bitte eine .wav-Datei wählen (z. B. mit Audacity als WAV exportieren).");
        var awc = LoadAwc(awcFile);
        var st = GetStream(awc, index);
        if (st.MidiChunk != null) throw new UserError("MIDI-Spuren können nicht ersetzt werden.");
        int rate = st.SamplesPerSecond;
        int samples = (int)(st.FormatChunk?.Samples ?? st.StreamFormat?.Samples ?? 0);
        if (rate <= 0 || samples <= 0) throw new UserError("Format des Original-Sounds unbekannt.");

        var (pcm, srcRate) = WavReader.ReadMono(File.ReadAllBytes(wavFile));
        var resampled = WavReader.Resample(pcm, srcRate, rate);
        bool adjusted = resampled.Length != samples;
        var fitted = new short[samples];
        Array.Copy(resampled, fitted, Math.Min(samples, resampled.Length));

        var tmp = Textures.TempDir();
        try
        {
            var xml = AwcXml.GetXml(awc, tmp);
            var target = Path.Combine(tmp, (st.Name?.Replace("/", "").Replace("\\", "") ?? "0x0") + ".wav");
            if (!File.Exists(target)) throw new UserError("Interner Fehler: WAV-Zwischendatei fehlt.");
            File.WriteAllBytes(target, WavReader.WriteMono16(fitted, rate));
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var rebuilt = XmlAwc.GetAwc(doc, tmp);
            var bytes = rebuilt.Save();
            Session.Backup(awcFile, backupDir);
            File.WriteAllBytes(awcFile, bytes);
        }
        finally { Textures.TryDelete(tmp); }

        return new
        {
            name = st.Name,
            adjusted,
            note = adjusted
                ? $"Die Länge wurde an das Original angepasst ({samples / (double)rate:0.00} s), damit der Sound im Spiel gültig bleibt."
                : null,
        };
    }

    // ------------------------------------------------------------------ .rel als XML bearbeiten

    public static object RelXmlGet(string relFile)
    {
        var rel = LoadRel(relFile);
        return new { xml = RelXml.GetXml(rel) };
    }

    public static object RelXmlSet(string relFile, string xml, string backupDir)
    {
        RelFile rel;
        try { rel = XmlRel.GetRel(xml); }
        catch (Exception ex) { throw new UserError($"Das XML ist fehlerhaft: {ex.Message}"); }
        if (rel == null) throw new UserError("Das XML konnte nicht umgewandelt werden.");
        var bytes = rel.Save();
        Session.Backup(relFile, backupDir);
        File.WriteAllBytes(relFile, bytes);
        return new { ok = true, size = bytes.Length };
    }
}

/// <summary>Robuster WAV-Leser (PCM 8/16/24/32, Float, Extensible, beliebige Zusatz-Chunks).</summary>
public static class WavReader
{
    public static (short[] pcm, int rate) ReadMono(byte[] data)
    {
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
            throw new UserError("Keine gültige WAV-Datei.");
        int pos = 12;
        int fmt = 0, channels = 0, rate = 0, bits = 0;
        byte[] raw = null;
        while (pos + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, pos, 4);
            int len = BitConverter.ToInt32(data, pos + 4);
            int body = pos + 8;
            if (len < 0 || body + len > data.Length) len = data.Length - body;
            if (id == "fmt ")
            {
                fmt = BitConverter.ToUInt16(data, body);
                channels = BitConverter.ToUInt16(data, body + 2);
                rate = BitConverter.ToInt32(data, body + 4);
                bits = BitConverter.ToUInt16(data, body + 14);
                if (fmt == 0xFFFE && len >= 26) fmt = BitConverter.ToUInt16(data, body + 24); // Extensible → SubFormat
            }
            else if (id == "data")
            {
                raw = new byte[len];
                Buffer.BlockCopy(data, body, raw, 0, len);
            }
            pos = body + len + (len & 1);
        }
        if (raw == null || channels <= 0 || rate <= 0) throw new UserError("WAV-Datei ohne Audiodaten.");
        if (fmt != 1 && fmt != 3) throw new UserError("Nur unkomprimierte WAV-Dateien werden unterstützt (PCM oder Float).");
        int bps = bits / 8;
        int frames = raw.Length / (bps * channels);
        var o = new short[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int i = (f * channels + c) * bps;
                double v = fmt == 3
                    ? (bps == 8 ? BitConverter.ToDouble(raw, i) : BitConverter.ToSingle(raw, i))
                    : bps switch
                    {
                        1 => (raw[i] - 128) / 128.0,
                        2 => BitConverter.ToInt16(raw, i) / 32768.0,
                        3 => ((raw[i] | (raw[i + 1] << 8) | ((sbyte)raw[i + 2] << 16))) / 8388608.0,
                        4 => BitConverter.ToInt32(raw, i) / 2147483648.0,
                        _ => 0,
                    };
                sum += v;
            }
            o[f] = (short)Math.Clamp(Math.Round(sum / channels * 32767.0), short.MinValue, short.MaxValue);
        }
        return (o, rate);
    }

    public static short[] Resample(short[] src, int from, int to)
    {
        if (from == to || src.Length == 0) return src;
        int n = (int)Math.Round(src.Length * (double)to / from);
        var o = new short[n];
        double step = (double)from / to;
        for (int i = 0; i < n; i++)
        {
            double p = i * step;
            int a = (int)p;
            double t = p - a;
            short s0 = src[Math.Min(a, src.Length - 1)], s1 = src[Math.Min(a + 1, src.Length - 1)];
            o[i] = (short)(s0 + (s1 - s0) * t);
        }
        return o;
    }

    /// <summary>Schlichte WAV (fmt direkt vor data) – so verlangt es CodeWalker.</summary>
    public static byte[] WriteMono16(short[] pcm, int rate)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int dataLen = pcm.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
        foreach (var s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
