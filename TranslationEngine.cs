using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SinhalaTypeBypasser
{
    public class SinhalaMapEntry
    {
        public string uni { get; set; } = "";
        public string fm { get; set; } = "";
        public string dl { get; set; } = "";
        public string isi { get; set; } = "";
        public string wije_uni { get; set; } = "";
        public string wije_legacy { get; set; } = "";
        public List<string> phonetic { get; set; } = new();
        public bool is_modifier { get; set; } = false;
        public bool is_conjunct { get; set; } = false;
        public bool is_symbol { get; set; } = false;
    }

    public enum InputStyle
    {
        ModernWijesekara,      // Consonant first, then Kombuva (l + f -> කෙ)
        TraditionalWijesekara, // Kombuva first, then Consonant (f + l -> කෙ)
        PhoneticSinglish       // Singlish phonetic (ka + ra + n + nE -> කරන්නේ)
    }

    public enum OutputFontFamily
    {
        DL,      // DL-Manel, DL-Araliya
        FM,      // FM-Abhaya, FM-Malithi
        ISI,     // ISI-Sinhala
        Unicode  // Iskoola Potha / Standard Unicode
    }

    public class EngineAction
    {
        public bool Suppress { get; set; } = false;
        public int BackspaceCount { get; set; } = 0;
        public string TextToInject { get; set; } = "";
        public string ActiveBufferPreview { get; set; } = "";
    }

    public class TranslationEngine
    {
        private List<SinhalaMapEntry> _entries = new();

        private List<(string Key, SinhalaMapEntry Entry)> _wijeUniSorted = new();
        private List<(string Key, SinhalaMapEntry Entry)> _wijeLegacySorted = new();
        private List<(string Key, SinhalaMapEntry Entry)> _phoneticSorted = new();
        private Dictionary<char, SinhalaMapEntry> _symbolMap = new();
        private HashSet<char> _validWijeChars = new();

        private string _keystrokeBuffer = "";
        private string _lastEmittedText = "";
        private bool _escapeNextAsSymbol = false;

        public bool IsActive { get; set; } = true;
        public InputStyle CurrentInputStyle { get; set; } = InputStyle.ModernWijesekara;
        public OutputFontFamily CurrentOutputFont { get; set; } = OutputFontFamily.DL;

        public int LoadedRulesCount => _entries.Count;

        public void LoadMasterDictionary()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "data_list.json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Could not find master dictionary at: {path}");

            string json = File.ReadAllText(path);
            _entries = JsonSerializer.Deserialize<List<SinhalaMapEntry>>(json) ?? new();

            _wijeUniSorted.Clear();
            _wijeLegacySorted.Clear();
            _phoneticSorted.Clear();
            _symbolMap.Clear();
            _validWijeChars.Clear();

            var seenWijeUni = new HashSet<string>(StringComparer.Ordinal);
            var seenWijeLegacy = new HashSet<string>(StringComparer.Ordinal);
            var seenPhonetic = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in _entries)
            {
                if (entry.is_symbol && entry.uni.Length == 1)
                {
                    _symbolMap[entry.uni[0]] = entry;
                    continue;
                }

                if (entry.uni.EndsWith('\u0DDF') || entry.uni.EndsWith('\u0DF3'))
                    continue;

                if (!string.IsNullOrEmpty(entry.wije_uni) && seenWijeUni.Add(entry.wije_uni))
                {
                    _wijeUniSorted.Add((entry.wije_uni, entry));
                    foreach (char c in entry.wije_uni)
                        _validWijeChars.Add(c);
                }

                if (!string.IsNullOrEmpty(entry.wije_legacy) && seenWijeLegacy.Add(entry.wije_legacy))
                {
                    _wijeLegacySorted.Add((entry.wije_legacy, entry));
                    foreach (char c in entry.wije_legacy)
                        _validWijeChars.Add(c);
                }

                if (entry.phonetic != null && !entry.is_modifier)
                {
                    foreach (var p in entry.phonetic.Where(p => !string.IsNullOrEmpty(p)))
                    {
                        if (seenPhonetic.Add(p))
                        {
                            _phoneticSorted.Add((p, entry));
                        }
                    }
                }
            }

            _wijeUniSorted = _wijeUniSorted
                .OrderByDescending(x => x.Key.Length)
                .ThenBy(x => x.Entry.is_modifier ? 1 : 0)
                .ToList();

            _wijeLegacySorted = _wijeLegacySorted
                .OrderByDescending(x => x.Key.Length)
                .ThenBy(x => x.Entry.is_modifier ? 1 : 0)
                .ToList();

            _phoneticSorted = _phoneticSorted
                .OrderByDescending(x => x.Key.Length)
                .ThenBy(x => x.Entry.is_conjunct ? 1 : 0)
                .ToList();
        }

        public EngineAction ProcessKeyPress(char keyChar)
        {
            if (!IsActive)
                return new EngineAction { Suppress = false };

            // 1. Handle active '\' Symbol Escape state
            if (_escapeNextAsSymbol)
            {
                _escapeNextAsSymbol = false;

                // Double backslash (\\) -> emit literal '\'
                if (keyChar == '\\')
                {
                    ResetBuffer();
                    return new EngineAction
                    {
                        Suppress = true,
                        BackspaceCount = 0,
                        TextToInject = "\\",
                        ActiveBufferPreview = @"\\ → \"
                    };
                }

                // Exception: in Wijesekara mode, if buffer ends with 'l' and user typed '\' then 'I',
                // let it form 'ක්‍ෂ' (l\I) instead of escaping 'I'
                if (CurrentInputStyle != InputStyle.PhoneticSinglish &&
                    keyChar == 'I' &&
                    _keystrokeBuffer.EndsWith("l", StringComparison.Ordinal))
                {
                    _keystrokeBuffer += "\\" + keyChar;
                    return EmitBufferDiff();
                }

                // Otherwise, flush word buffer and emit the escaped symbol
                ResetBuffer();
                return EmitEscapedSymbol(keyChar);
            }

            // 2. Activate '\' Symbol Escape when '\' is pressed
            if (keyChar == '\\')
            {
                _escapeNextAsSymbol = true;
                return new EngineAction
                {
                    Suppress = true,
                    BackspaceCount = 0,
                    TextToInject = "",
                    ActiveBufferPreview = @"Symbol Escape (\)..."
                };
            }

            // 3. Whitespace flushes buffer
            if (char.IsWhiteSpace(keyChar))
            {
                ResetBuffer();
                return new EngineAction { Suppress = false };
            }

            // 4. Validate character for active input mode
            if (CurrentInputStyle == InputStyle.PhoneticSinglish)
            {
                if (!char.IsLetter(keyChar))
                {
                    ResetBuffer();

                    // In Singlish mode, punctuation keys also map directly
                    if (_symbolMap.TryGetValue(keyChar, out var symEntry) && CurrentOutputFont != OutputFontFamily.Unicode)
                    {
                        string mappedSym = GetTargetGlyph(symEntry);
                        return new EngineAction
                        {
                            Suppress = true,
                            BackspaceCount = 0,
                            TextToInject = mappedSym,
                            ActiveBufferPreview = $"{keyChar} → {mappedSym}"
                        };
                    }

                    return new EngineAction { Suppress = false };
                }
            }
            else
            {
                if (!_validWijeChars.Contains(keyChar))
                {
                    ResetBuffer();
                    return new EngineAction { Suppress = false };
                }
            }

            _keystrokeBuffer += keyChar;

            if (_keystrokeBuffer.Length > 40)
            {
                ResetBuffer();
                _keystrokeBuffer += keyChar;
            }

            return EmitBufferDiff();
        }

        private EngineAction EmitEscapedSymbol(char physicalSymbol)
        {
            if (_symbolMap.TryGetValue(physicalSymbol, out var entry))
            {
                string targetGlyph = GetTargetGlyph(entry);
                return new EngineAction
                {
                    Suppress = true,
                    BackspaceCount = 0,
                    TextToInject = targetGlyph,
                    ActiveBufferPreview = $"\\{physicalSymbol} → {targetGlyph}"
                };
            }

            return new EngineAction
            {
                Suppress = true,
                BackspaceCount = 0,
                TextToInject = physicalSymbol.ToString(),
                ActiveBufferPreview = $"\\{physicalSymbol} → {physicalSymbol}"
            };
        }

        private EngineAction EmitBufferDiff()
        {
            string converted = ConvertBuffer(_keystrokeBuffer);

            int commonPrefix = 0;
            int minLen = Math.Min(_lastEmittedText.Length, converted.Length);
            while (commonPrefix < minLen && _lastEmittedText[commonPrefix] == converted[commonPrefix])
            {
                commonPrefix++;
            }

            int backspacesNeeded = _lastEmittedText.Length - commonPrefix;
            string textToInject = converted.Substring(commonPrefix);

            _lastEmittedText = converted;

            return new EngineAction
            {
                Suppress = true,
                BackspaceCount = backspacesNeeded,
                TextToInject = textToInject,
                ActiveBufferPreview = $"{_keystrokeBuffer} → {converted}"
            };
        }

        private string ConvertBuffer(string input)
        {
            var table = CurrentInputStyle switch
            {
                InputStyle.ModernWijesekara => _wijeUniSorted,
                InputStyle.TraditionalWijesekara => _wijeLegacySorted,
                InputStyle.PhoneticSinglish => _phoneticSorted,
                _ => _wijeUniSorted
            };

            StringBuilder output = new();
            int i = 0;

            while (i < input.Length)
            {
                bool matched = false;
                string remaining = input.Substring(i);

                foreach (var item in table)
                {
                    if (remaining.StartsWith(item.Key, StringComparison.Ordinal))
                    {
                        output.Append(GetTargetGlyph(item.Entry));
                        i += item.Key.Length;
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    output.Append(input[i]);
                    i++;
                }
            }

            return output.ToString();
        }

        private string GetTargetGlyph(SinhalaMapEntry entry)
        {
            string raw = CurrentOutputFont switch
            {
                OutputFontFamily.DL => !string.IsNullOrEmpty(entry.dl) ? entry.dl : entry.fm,
                OutputFontFamily.FM => entry.fm,
                OutputFontFamily.ISI => entry.isi,
                OutputFontFamily.Unicode => entry.uni,
                _ => entry.dl
            };

            if (CurrentOutputFont != OutputFontFamily.Unicode)
            {
                StringBuilder safe = new(raw.Length);
                foreach (char c in raw)
                {
                    if (c < '\u0D80' || c > '\u0DFF')
                        safe.Append(c);
                }
                return safe.ToString();
            }

            return raw;
        }

        public void ResetBuffer()
        {
            _keystrokeBuffer = "";
            _lastEmittedText = "";
            _escapeNextAsSymbol = false;
        }
    }
}