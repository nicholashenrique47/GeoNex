using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace GeoNex.Core.Symbology
{
    public class DataDefinedProperty
    {
        public string PropertyKey { get; set; } = string.Empty;
        public bool Enabled { get; set; } = false;
        public string FieldName { get; set; } = string.Empty;
        public string Expression { get; set; } = string.Empty;

        public DataDefinedProperty Clone()
        {
            return new DataDefinedProperty
            {
                PropertyKey = PropertyKey,
                Enabled = Enabled,
                FieldName = FieldName,
                Expression = Expression
            };
        }

        public object? Evaluate(IReadOnlyDictionary<string, object?>? attributes)
        {
            if (!Enabled || attributes == null || string.IsNullOrWhiteSpace(FieldName))
                return null;

            if (attributes.TryGetValue(FieldName, out var val))
                return val;

            return null;
        }

        public float EvaluateFloat(IReadOnlyDictionary<string, object?>? attributes, float fallback)
        {
            var raw = Evaluate(attributes);
            if (raw == null) return fallback;

            if (raw is float f) return f;
            if (raw is double d) return (float)d;
            if (raw is int i) return i;
            if (raw is long l) return l;

            if (float.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return parsed;

            return fallback;
        }

        public SKColor EvaluateColor(IReadOnlyDictionary<string, object?>? attributes, SKColor fallback)
        {
            var raw = Evaluate(attributes);
            if (raw == null) return fallback;

            if (raw is SKColor c) return c;
            string str = raw.ToString() ?? string.Empty;
            if (SKColor.TryParse(str, out SKColor parsed))
                return parsed;

            return fallback;
        }

        public string? EvaluateString(IReadOnlyDictionary<string, object?>? attributes, string? fallback)
        {
            var raw = Evaluate(attributes);
            return raw?.ToString() ?? fallback;
        }
    }

    /// <summary>
    /// Coleção de vínculos dinâmicos entre atributos geográficos e parâmetros visuais.
    /// </summary>
    public class DataDefinedPropertyCollection
    {
        private readonly Dictionary<string, DataDefinedProperty> _properties = new(StringComparer.OrdinalIgnoreCase);

        public DataDefinedProperty GetOrCreate(string propertyKey)
        {
            if (!_properties.TryGetValue(propertyKey, out var prop))
            {
                prop = new DataDefinedProperty { PropertyKey = propertyKey };
                _properties[propertyKey] = prop;
            }
            return prop;
        }

        public bool TryGet(string propertyKey, out DataDefinedProperty? property)
        {
            return _properties.TryGetValue(propertyKey, out property);
        }

        public void Set(string propertyKey, string fieldName, bool enabled = true)
        {
            var prop = GetOrCreate(propertyKey);
            prop.FieldName = fieldName;
            prop.Enabled = enabled;
        }

        public DataDefinedPropertyCollection Clone()
        {
            var clone = new DataDefinedPropertyCollection();
            foreach (var kv in _properties)
            {
                clone._properties[kv.Key] = kv.Value.Clone();
            }
            return clone;
        }
    }
}
