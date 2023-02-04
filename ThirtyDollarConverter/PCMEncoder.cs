#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThirtyDollarEncoder.PCM;
using ThirtyDollarParser;

namespace ThirtyDollarConverter
{
    public class PcmEncoder
    {
        public PcmEncoder(SampleHolder samples, Composition composition, EncoderSettings settings, Action<string>? loggerAction = null,
            Action<int, int>? indexReport = null)
        {
            Holder = samples;
            Composition = composition;
            Log = loggerAction ?? new Action<string>(_ => { });
            IndexReport = indexReport ?? new Action<int, int>((_, _) => { });
            SampleRate = settings.SampleRate;
            Channels = settings.Channels;
            switch (Channels)
            {
                case < 1:
                    throw new Exception("Having less than one channel is literally impossible.");
                case > 2:
                    throw new Exception("Having more than two audio channels isn't currently supported.");
            }
        }
        
        private readonly int SampleRate;
        private readonly int Channels;
        private SampleHolder Holder { get; }
        public Composition Composition { get; }
        private Dictionary<Sound, PcmDataHolder> Samples => Holder.SampleList;
        private Action<string> Log { get; }
        private Action<int, int> IndexReport { get; }

        public AudioData<float> SampleComposition(Composition composition, int threadCount = -1)
        {
            var copy = composition.Copy(); // To avoid making any changes to the original composition.
            var placement = CalculatePlacement(copy);
            
            var (processedEvents, queue) = GetAudioSamples(threadCount, placement).Result;
            
            for (var i = 0; i < processedEvents.Count; i++)
            {
                var ev = processedEvents[i];
                Console.WriteLine(
                    $"({i}): Event: \'{ev.Name}\' {ev.AudioData.Samples[0].Length} - [{ev.Value}] - ({ev.Volume})");
            }

            Console.WriteLine("Constructing audio.");
            var audioData = GenerateAudioData(queue, processedEvents).Result;
            return audioData;
        }

        private async Task<Tuple<List<ProcessedEvent>, Queue<Placement>>> GetAudioSamples(int threadCount, IEnumerable<Placement> placement, 
            CancellationToken? cancellationToken = null)
        {
            var token = cancellationToken ?? CancellationToken.None;
            
            // Get host thread count or use specified count and make an array of threads.
            var processorCount = threadCount == -1 ? Environment.ProcessorCount : threadCount;
            var threads = new Task[processorCount];
            for (var i = 0; i < threads.Length; i++)
            {
                threads[i] = Task.CompletedTask;
            }

            var processedEvents = new List<ProcessedEvent>();

            // I want to avoid multiple enumeration of the Placement IEnumerable. (totally not because Rider complains about it. no....)
            var queue = new Queue<Placement>();

            var currentThread = 0;
            foreach (var current in placement)
            {
                // Wait for the previous thread to finish its work.
                await threads[currentThread].WaitAsync(token);

                var ev = current.Event;
                queue.Enqueue(current);
                if (ev.SoundEvent == "#!cut") continue;
                lock (processedEvents)
                {
                    if (processedEvents.Any(r => r.Name == ev.SoundEvent && Math.Abs(r.Value - ev.Value) < 1))
                        continue;
                }

                // Adding the event here to prevent any thread fighting.
                var processed = new ProcessedEvent
                {
                    Name = ev.SoundEvent ??
                           throw new Exception(
                               $"Event name is null at index: \'{current.Index}\' after placement pass."),
                    Value = ev.Value,
                    Volume = ev.Volume,
                    AudioData = AudioData<float>.Empty((uint) Channels)
                };

                var thread = threads[currentThread] = new Task(() =>
                {
                    processed.AudioData = HandleEvent(ev, (uint) Channels);
                    lock (processedEvents)
                    {
                        processedEvents.Add(processed);
                    }
                });
                thread.Start();

                if (++currentThread >= processorCount) currentThread = 0;
            }

            foreach (var thread in threads)
            {
                await thread.WaitAsync(token);
            }

            return new Tuple<List<ProcessedEvent>, Queue<Placement>>(processedEvents, queue);
        }

        private async Task<AudioData<float>> GenerateAudioData(IEnumerable<Placement> queue, IReadOnlyCollection<ProcessedEvent> processedEvents, 
            CancellationToken? cancellationToken = null)
        {
            var token = cancellationToken ?? CancellationToken.None;
            var audioData = AudioData<float>.Empty((uint) Channels);

            var encodeTasks = new Task[Channels];

            for (var j = 0; j < Channels; j++)
            {
                var i = j;
                encodeTasks[i] = new Task(() =>
                {
                    foreach (var thing in queue) // I can't name things...
                    {
                        //Console.WriteLine($"({i}) Processing: {thing.Index}");
                        var ev = thing.Event;
                        if (ev.SoundEvent == "#!cut")
                        {
                            var end = (ulong) audioData.Samples[i].LongLength;
                            lock (audioData.Samples[i])
                                for (var k = thing.Index; k < end; k++)
                                {
                                    audioData.Samples[i][k] = 0f;
                                }
                            continue;
                        }

                        var sample =
                            processedEvents.First(r => r.Name == ev.SoundEvent && Math.Abs(r.Value - ev.Value) < 1)
                                .AudioData;

                        var data = sample.GetChannel(i);
                        RenderSample(data, ref audioData.Samples[i], thing.Index, ev.Volume);
                    }
                });
                encodeTasks[i].Start();
            }

            foreach (var task in encodeTasks)
            {
                await task.WaitAsync(token);
            }

            return audioData;
        }

        public IEnumerable<Placement> CalculatePlacement(Composition composition)
        {
            if (composition == null) throw new Exception("Null Composition");
            var bpm = 300.0;
            var position = (ulong) (SampleRate / (bpm / 60));
            var transpose = 0.0;
            var volume = 100.0;

            for (var i = 0; i < composition!.Events.Count; i++)
            {
                var index = position;
                var ev = composition.Events[i];
                IndexReport(i, composition!.Events.Count);
                switch (ev.SoundEvent)
                {
                    case "!speed":
                        switch (ev.ValueScale)
                        {
                            case ValueScale.Times:
                                bpm *= ev.Value;
                                break;
                            case ValueScale.Add:
                                bpm += ev.Value;
                                break;
                            case ValueScale.None:
                                bpm = ev.Value;
                                break;
                        }

                        Log($"BPM is now: {bpm}");
                        continue;
                    
                    case "!volume":
                        switch (ev.ValueScale)
                        {
                            case ValueScale.Times:
                                volume *= ev.Value;
                                break;
                            case ValueScale.Add:
                                volume += ev.Value;
                                break;
                            case ValueScale.None:
                                volume = ev.Value;
                                break;
                        }
                        continue;

                    case "!loopmany" or "!loop":
                        if (ev.PlayTimes <= 0) continue;
                        ev.PlayTimes--;
                        for (var j = i; j > 0; j--)
                        {
                            if (composition.Events[j].SoundEvent != "!looptarget")
                            {
                                continue;
                            }

                            i = j - 1;
                            break;
                        }

                        Log($"Going to element: ({i + 1}) - \"{composition.Events[i + 1]}\"");
                        continue;

                    case "!jump":
                        if (ev.PlayTimes <= 0) continue;
                        ev.PlayTimes--;
                        //i = Triggers[(int) ev.Value - 1] - 1;
                        var item = composition.Events.FirstOrDefault(r =>
                            r.SoundEvent == "!target" && (int) r.Value == (int) ev.Value);
                        if (item == null)
                        {
                            Log($"Unable to jump to target with id: {ev.Value}");
                            continue;
                        }

                        i = composition.Events.IndexOf(item) - 1;
                        Log($"Jumping to element: ({i}) - {composition.Events[i]}");
                        //
                        continue;

                    case "_pause" or "!stop":
                        while (ev.PlayTimes >= 1)
                        {
                            ev.PlayTimes--;
                            position += (ulong) (SampleRate / (bpm / 60));
                        }

                        ev.PlayTimes = ev.OriginalLoop;
                        continue;

                    case "!cut":
                        yield return new Placement
                        {
                            Event = new Event
                            {
                                SoundEvent = "#!cut",
                                Value = index + SampleRate / (bpm / 60)
                            },
                            Index = index
                        };
                        
                        continue;

                    case "" or "!looptarget" or "!target" or "!volume" or "!flash" or "!bg":
                        continue;

                    case "!combine":
                        position -= (ulong) (SampleRate / (bpm / 60));
                        continue;

                    case "!transpose":
                        switch (ev.ValueScale)
                        {
                            case ValueScale.Times:
                                transpose *= ev.Value;
                                continue;
                            case ValueScale.Add:
                                transpose += ev.Value;
                                continue;
                            case ValueScale.None:
                                transpose = ev.Value;
                                continue;
                        }

                        continue;

                    default:
                        position += (ulong) (SampleRate / (bpm / 60));
                        break;
                }
                // To avoid modifying the original event.
                var copy = ev.Copy();
                copy.Volume = volume;
                copy.Value += transpose;
                yield return new Placement
                {
                    Index = index,
                    Event = copy
                };
                switch (ev.SoundEvent)
                {
                    case not ("!transpose" or "!loopmany" or "!volume" or "!flash" or "!combine" or "!speed" or
                        "!looptarget" or "!loop" or "!cut" or "!target" or "!jump" or "_pause" or "!stop"):
                        if (ev.PlayTimes > 1)
                        {
                            ev.PlayTimes--;
                            i--;
                            continue;
                        }

                        ev.PlayTimes = ev.OriginalLoop;
                        continue;
                }
            }
        }

        private struct ProcessedEvent
        {
            public string Name;
            public double Value;
            public double Volume;
            public AudioData<float> AudioData;
        }

        private AudioData<float> HandleEvent(Event ev, uint channelCount)
        {
            try
            {
                var (_, value) = Samples.AsParallel().FirstOrDefault(pair => pair.Key.Filename == ev.SoundEvent || pair.Key.Id == ev.SoundEvent);
                if (value == null)
                {
                    throw new Exception($"Sound Event: \'{ev.SoundEvent}\' is null.");
                }
                var sampleData = value.ReadAsFloat32Array(Channels > 1);
                if (sampleData == null)
                    throw new NullReferenceException(
                        $"Sample data is null for event: \"{ev}\", Samples Count is: {Samples.Count}");
                
                var audioData = new AudioData<float>(channelCount);
                
                for (var i = 0; i < channelCount; i++)
                {
                    audioData.Samples[i] = Resample(sampleData.GetChannel(i), value.SampleRate,
                        (uint) (SampleRate / Math.Pow(2, ev.Value / 12)), 1);
                }

                return audioData;
            }
            catch (Exception e)
            {
                Log($"Processing failed: \"{e}\"");
            }

            return AudioData<float>.Empty(channelCount);
        }


        private unsafe float[] Resample(float[] samples, uint sampleRate, uint targetSampleRate, uint channels)
        {
            if (sampleRate == targetSampleRate) return samples;
            fixed (float* vals = samples)
            {
                var length = Resample32BitFloat(vals, null, sampleRate, targetSampleRate, (ulong) samples.LongLength,
                    channels);
                float[] alloc = new float[length];
                fixed (float* output = alloc)
                {
                    Resample32BitFloat(vals, output, sampleRate, targetSampleRate, (ulong) samples.LongLength,
                        channels);
                }

                return alloc;
            }
        }

        // Original Source: https://github.com/cpuimage/resampler

        private unsafe ulong Resample32BitFloat(float* input, float* output, uint inSampleRate, uint outSampleRate,
            ulong inputSize, uint channels)
        {
            if (input == null) return 0;
            var outputSize = (ulong) (inputSize * (double) outSampleRate / inSampleRate);
            outputSize -= outputSize % channels;
            if (output == null) return outputSize;
            var stepDist = inSampleRate / (double) outSampleRate;
            const ulong fixedFraction = (ulong) 1 << 32;
            const double normFixed = 1.0 / ((ulong) 1 << 32);
            var step = (ulong) (stepDist * fixedFraction + 0.5);
            ulong curOffset = 0;
            for (uint i = 0; i < outputSize; i += 1)
            {
                for (uint c = 0; c < channels; c += 1)
                {
                    *output++ = (float) (input[c] + (input[c + channels] - input[c]) *
                        ((curOffset >> 32) + (curOffset & (fixedFraction - 1)) * normFixed));
                }

                curOffset += step;
                input += (curOffset >> 32) * channels;
                curOffset &= fixedFraction - 1;
            }

            return outputSize;
        }

        private unsafe ulong Resample16Bit(short* input, short* output, uint inSampleRate, uint outSampleRate,
            ulong inputSize, uint channels)
        {
            var outputSize = (ulong) (inputSize * (double) outSampleRate / inSampleRate);
            outputSize -= outputSize % channels;
            if (output == null) return outputSize;
            var stepDist = (double) inSampleRate / outSampleRate;
            const ulong fixedFraction = (ulong) 1 << 32;
            const double normFixed = 1.0 / ((ulong) 1 << 32);
            var step = (ulong) (stepDist * fixedFraction + 0.5);
            ulong curOffset = 0;
            for (uint i = 0; i < outputSize; i += 1)
            {
                for (uint c = 0; c < channels; c += 1)
                    *output++ = (short) (input[c] + (input[c + channels] - input[c]) *
                        ((curOffset >> 32) + (curOffset & (fixedFraction - 1)) * normFixed));
                curOffset += step;
                input += (curOffset >> 32) * channels;
                curOffset &= fixedFraction - 1;
            }

            return outputSize;
        }

        public void WriteAsWavFile(string location, AudioData<float> data)
        {
            var samples = data.Samples;
            for (var i = 0; i < samples.Length; i++)
            {
                var arr = samples[i];
                arr.NormalizeVolume();
                samples[i] = arr.TrimEnd();
            }

            var stream = new BinaryWriter(File.Open(location, FileMode.Create));
            var maxLength = samples.Max(r => r.Length);
            AddWavHeader(stream, maxLength);
            stream.Write((short) 0);

            for (var i = 0; i < maxLength; i++)
            {
                for (var j = 0; j < Channels; j++)
                {
                    if (samples[j].Length > i)
                        stream.Write((short) (samples[j][i] * 32768));
                    else stream.Write((short) 0);
                }
            }

            stream.Close();
        }

        /*public MemoryStream WriteAsWavStream()
        {
            var ms = new MemoryStream();
            PcmBytes.NormalizeVolume();
            PcmBytes = PcmBytes.TrimEnd();
            var stream = new BinaryWriter(ms);
            AddWavHeader(stream);
            stream.Write((short) 0);
            foreach (var data in PcmBytes)
            {
                for (var i = 0; i < Channels; i++)
                {
                    stream.Write((short) (data * 32768));
                }
            }
            stream.Close();
            return ms;
        }*/

        private void AddWavHeader(BinaryWriter writer, int dataLength)
        {
            var length = dataLength * Channels;
            writer.Write(new[] {'R', 'I', 'F', 'F'}); // RIFF Chunk Descriptor
            writer.Write(4 + 8 + 16 + 8 + length * 2); // Sub Chunk 1 Size
            //Chunk Size 4 bytes.
            writer.Write(new[] {'W', 'A', 'V', 'E'});
            // fmt sub-chunk
            writer.Write(new[] {'f', 'm', 't', ' '});
            writer.Write(16); // Sub Chunk 1 Size
            writer.Write((short) 1); // Audio Format 1 = PCM
            writer.Write((short) Channels); // Audio Channels
            writer.Write(SampleRate); // Sample Rate
            writer.Write(SampleRate * Channels * 2 /* Bytes */); // Byte Rate
            writer.Write((short) (Channels * 2)); // Block Align
            writer.Write((short) 16); // Bits per Sample
            // data sub-chunk
            writer.Write(new[] {'d', 'a', 't', 'a'});
            writer.Write(length * 2); // Sub Chunk 2 Size.
        }
        
        #region Sample Processing Methods

        private static void RenderSample(float[] source, ref float[] destination, ulong index, double volume)
        {
            lock (destination)
            {
                for (ulong i = 0; i < (ulong) source.LongLength; i++)
                {
                    var data = source[i];
                    ModifyAt(ref destination, (float) (data * (volume / 100)), index + i);
                }
            }
        }
        
        private static void ModifyAt(ref float[] destination, float data, ulong index)
        {
            lock (destination)
            {
                if (index < (ulong) destination.LongLength)
                {
                    destination[index] = MixSamples(data, destination[index]);
                    return;
                }

                if (index >= (ulong) destination.LongLength) FillWithZeros(ref destination, index);
                destination[index] = data;
            }
        }

        private static float MixSamples(float sampleOne, float sampleTwo)
        {
            return sampleOne + sampleTwo;
        }
        
        
        private static void FillWithZeros(ref float[] data, ulong index)
        {
            var old = data;
            data = new float[(ulong) (index * 1.5)];
            for (ulong i = 0; i < (ulong) old.LongLength; i++)
            {
                data[i] = old[i];
            }
        }

        #endregion
    }
}