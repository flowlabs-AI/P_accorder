namespace Accorder;

public readonly record struct Note(string Name, string FrenchName, int Octave, float TargetFreq, float Cents)
{
    public const float DefaultReferenceA4 = 440f;

    private static readonly string[] Names =
        { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly string[] FrenchNames =
        { "Do", "Do#", "Ré", "Ré#", "Mi", "Fa", "Fa#", "Sol", "Sol#", "La", "La#", "Si" };

    public static Note FromFrequency(float freq, float referenceA4 = DefaultReferenceA4)
    {
        double midi = 69 + 12 * Math.Log2(freq / referenceA4);
        int nearest = (int)Math.Round(midi);
        float target = (float)(referenceA4 * Math.Pow(2, (nearest - 69) / 12.0));
        float cents = (float)(1200 * Math.Log2(freq / target));
        int idx = ((nearest % 12) + 12) % 12;
        int octave = nearest / 12 - 1;
        return new Note(Names[idx], FrenchNames[idx], octave, target, cents);
    }
}
