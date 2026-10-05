namespace Accorder;

/// <summary>
/// Détection de hauteur par l'algorithme YIN (de Cheveigné & Kawahara, 2002).
/// </summary>
public sealed class PitchDetector
{
    private readonly int _sampleRate;
    private readonly int _windowSize;
    private readonly int _tauMin;
    private readonly int _tauMax;
    private readonly float[] _diff;
    private const float Threshold = 0.15f;

    public PitchDetector(int sampleRate, int bufferSize, float minFreq = 60f, float maxFreq = 1200f)
    {
        _sampleRate = sampleRate;
        _windowSize = bufferSize / 2;
        _tauMin = Math.Max(2, (int)(sampleRate / maxFreq));
        _tauMax = Math.Min(_windowSize - 1, (int)(sampleRate / minFreq));
        _diff = new float[_tauMax + 2];
    }

    /// <summary>Retourne la fréquence fondamentale en Hz, ou null si aucune note nette.</summary>
    public float? Detect(float[] buffer)
    {
        // 1. Fonction de différence
        for (int tau = 1; tau <= _tauMax + 1; tau++)
        {
            float sum = 0;
            for (int j = 0; j < _windowSize; j++)
            {
                float delta = buffer[j] - buffer[j + tau];
                sum += delta * delta;
            }
            _diff[tau] = sum;
        }

        // 2. Différence moyenne normalisée cumulative
        _diff[0] = 1;
        float running = 0;
        for (int tau = 1; tau <= _tauMax + 1; tau++)
        {
            running += _diff[tau];
            _diff[tau] = running > 0 ? _diff[tau] * tau / running : 1;
        }

        // 3. Seuil absolu : premier minimum sous le seuil
        int best = -1;
        for (int tau = _tauMin; tau <= _tauMax; tau++)
        {
            if (_diff[tau] < Threshold)
            {
                while (tau + 1 <= _tauMax && _diff[tau + 1] < _diff[tau]) tau++;
                best = tau;
                break;
            }
        }
        if (best < 0) return null;

        // 4. Interpolation parabolique pour la précision sub-échantillon
        float betterTau = best;
        if (best > 1 && best < _tauMax)
        {
            float s0 = _diff[best - 1], s1 = _diff[best], s2 = _diff[best + 1];
            float denom = 2 * (2 * s1 - s2 - s0);
            if (Math.Abs(denom) > 1e-9f)
                betterTau = best + (s2 - s0) / denom;
        }

        return _sampleRate / betterTau;
    }
}
