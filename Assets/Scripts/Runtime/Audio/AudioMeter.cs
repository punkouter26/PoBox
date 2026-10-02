using UnityEngine;

namespace PoBox.Audio
{
    /// <summary>
    /// Sits beside the AudioListener and does two things to the finished mix, in the audio thread.
    ///
    /// It measures it: the peak and the average level of what is actually going to the speakers, in
    /// decibels below full scale, and how many samples would have clipped. Those are the numbers on the
    /// debug panel, and what a bout's sound is checked against when nobody is there to listen to it.
    ///
    /// And it limits it: when a knockdown lands on top of a crowd roar and a bell, the sum goes past full
    /// scale, and without this the peaks would be cut off square. The gain comes down at once to keep the
    /// loudest sample at -1 dB, and is let back up over a fifth of a second.
    /// </summary>
    [RequireComponent(typeof(AudioListener))]
    public class AudioMeter : MonoBehaviour
    {
        public static AudioMeter Instance { get; private set; }

        [Tooltip("The mix is held under this, as a share of full scale (0.89 is -1 dB).")]
        [Range(0.5f, 1f)] public float ceiling = 0.89f;
        [Tooltip("Seconds for the gain to come back after a peak.")]
        public float release = 0.2f;
        public bool limit = true;

        /// <summary>Loudest sample of the last quarter second, before the limiter, dB below full scale (0 is full).</summary>
        public float PeakDb => ToDb(_peakHold);
        /// <summary>Average level over the last second or so, dB below full scale.</summary>
        public float RmsDb => ToDb(Mathf.Sqrt(_meanSquare));
        /// <summary>The most the limiter has had to take off, dB, since <see cref="ResetStats"/>.</summary>
        public float MaxReductionDb => -ToDb(_minGain);
        /// <summary>The loudest sample since <see cref="ResetStats"/>, before the limiter, dB.</summary>
        public float MaxPeakDb => ToDb(_maxPeak);
        /// <summary>Samples that were over full scale before the limiter, since <see cref="ResetStats"/>.</summary>
        public long OverCount => _over;

        // Written by the audio thread, read by the main one: single floats and a long, so a torn read is
        // at worst one stale number on a debug panel.
        volatile float _peakHold, _meanSquare, _gain = 1f, _minGain = 1f, _maxPeak;
        long _over;
        int _sampleRate = 48000;

        void Awake()
        {
            Instance = this;
            _sampleRate = AudioSettings.outputSampleRate;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void ResetStats()
        {
            _minGain = 1f;
            _maxPeak = 0f;
            System.Threading.Interlocked.Exchange(ref _over, 0);
        }

        static float ToDb(float linear) => linear > 1e-5f ? 20f * Mathf.Log10(linear) : -100f;

        void OnAudioFilterRead(float[] data, int channels)
        {
            int frames = data.Length / Mathf.Max(1, channels);
            if (frames == 0) return;
            float peak = 0f, sum = 0f;
            long over = 0;
            float gain = _gain;
            float up = 1f / Mathf.Max(1f, release * _sampleRate);      // gain regained per frame
            for (int f = 0; f < frames; f++)
            {
                int at = f * channels;
                float loudest = 0f;
                for (int c = 0; c < channels; c++)
                {
                    float s = data[at + c];
                    float a = s < 0f ? -s : s;
                    if (a > loudest) loudest = a;
                    sum += s * s;
                }
                if (loudest > peak) peak = loudest;
                if (loudest > 1f) over++;
                if (!limit) continue;
                // Down at once, back up slowly.
                float allowed = loudest > ceiling ? ceiling / loudest : 1f;
                gain = allowed < gain ? allowed : Mathf.Min(1f, gain + up);
                if (gain < 0.9999f)
                    for (int c = 0; c < channels; c++) data[at + c] *= gain;
            }
            _gain = gain;
            if (gain < _minGain) _minGain = gain;
            if (peak > _maxPeak) _maxPeak = peak;
            if (over > 0) System.Threading.Interlocked.Add(ref _over, over);

            // A buffer is about 20 ms: the peak is held and let fall, the average is over about a second.
            float decay = Mathf.Exp(-frames / (0.25f * _sampleRate));
            _peakHold = Mathf.Max(peak, _peakHold * decay);
            float k = Mathf.Clamp01(frames / (float)_sampleRate);
            _meanSquare += (sum / (frames * channels) - _meanSquare) * k;
        }
    }
}
