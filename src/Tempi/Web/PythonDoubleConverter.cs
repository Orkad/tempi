using System.Text.Json;
using System.Text.Json.Serialization;
using Tempi.Configuration;

namespace Tempi.Web;

/// <summary>
/// Sérialise les flottants comme <c>json.dumps</c> de Python.
/// </summary>
/// <remarks>
/// Python écrit <c>5.0</c> là où System.Text.Json écrit <c>5</c> : la valeur relue est
/// la même, mais l'octet ne l'est pas. Comme l'équivalence des deux implémentations se
/// démontre par comparaison octet à octet, la différence doit disparaître à la source
/// plutôt que d'être excusée dans l'outil de comparaison.
/// </remarks>
internal sealed class PythonDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            // json.dumps écrit NaN et Infinity sans guillemets, ce que JSON interdit ;
            // aucune température ne peut prendre ces valeurs, mais mieux vaut rendre
            // quelque chose de valide que de produire un document illisible.
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(PythonRepr.Number(value), skipInputValidation: true);
    }
}
