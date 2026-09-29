using System.Runtime.InteropServices;

namespace TextSpike;

/// <summary>Extended Linguistic Services (elscore.dll): MappingGetServices,
/// MappingRecognizeText, MappingFreePropertyBag, MappingFreeServices. Used
/// for its Language Detection service and its Hans/Hant transliteration
/// services.</summary>
internal static unsafe partial class Els
{
    private const string Lib = "elscore.dll";
    public static readonly Guid LanguageDetection = new("CF7E00B1-909B-4D95-A8F4-611F7C377702"); // ELS_GUID_LANGUAGE_DETECTION

    [StructLayout(LayoutKind.Sequential)]
    private struct MappingEnumOptions
    {
        public nuint Size;
        public IntPtr pszCategory, pszInputLanguage, pszOutputLanguage, pszInputScript, pszOutputScript, pszInputContentType, pszOutputContentType;
        public Guid* pGuid;
        public uint Flags; // OnlineService:2, ServiceType:2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MappingServiceInfo
    {
        public nuint Size;
        public IntPtr pszCopyright;
        public ushort wMajorVersion, wMinorVersion, wBuildVersion, wStepVersion;
        public uint dwInputContentTypesCount; public IntPtr prgInputContentTypes;
        public uint dwOutputContentTypesCount; public IntPtr prgOutputContentTypes;
        public uint dwInputLanguagesCount; public IntPtr prgInputLanguages;
        public uint dwOutputLanguagesCount; public IntPtr prgOutputLanguages;
        public uint dwInputScriptsCount; public IntPtr prgInputScripts;
        public uint dwOutputScriptsCount; public IntPtr prgOutputScripts;
        public Guid guid;
        public IntPtr pszCategory, pszDescription;
        public uint dwPrivateDataSize; public IntPtr pPrivateData;
        public IntPtr pContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappingDataRange
    {
        public uint dwStartIndex, dwEndIndex;
        public IntPtr pszDescription; public uint dwDescriptionLength;
        public IntPtr pData; public uint dwDataSize;
        public IntPtr pszContentType;
        public IntPtr prgActionIds; public uint dwActionsCount;
        public IntPtr prgActionDisplayNames;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappingPropertyBag
    {
        public nuint Size;
        public MappingDataRange* prgResultRanges; public uint dwRangesCount;
        public IntPtr pServiceData; public uint dwServiceDataSize;
        public IntPtr pCallerData; public uint dwCallerDataSize;
        public IntPtr pContext;
    }

    [LibraryImport(Lib)]
    private static partial int MappingGetServices(MappingEnumOptions* options, MappingServiceInfo** services, uint* count);

    [LibraryImport(Lib)]
    private static partial int MappingFreeServices(MappingServiceInfo* services);

    [LibraryImport(Lib)]
    private static partial int MappingRecognizeText(MappingServiceInfo* service, char* text, uint length, uint index, IntPtr options, MappingPropertyBag* bag);

    [LibraryImport(Lib)]
    private static partial int MappingFreePropertyBag(MappingPropertyBag* bag);

    public sealed record ServiceSummary(Guid Guid, string Category, string Description, string Version, string InputScripts, string OutputScripts);

    private static string Str(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p) ?? "";

    private static string StrArray(IntPtr arr, uint count)
    {
        var items = new List<string>();
        for (int i = 0; i < count; i++) items.Add(Str(((IntPtr*)arr)[i]));
        return string.Join(",", items);
    }

    public static List<ServiceSummary> AllServices()
    {
        var list = new List<ServiceSummary>();
        MappingServiceInfo* services = null;
        uint count = 0;
        var opt = new MappingEnumOptions { Size = (nuint)sizeof(MappingEnumOptions) };
        int hr = MappingGetServices(&opt, &services, &count);
        if (hr < 0) throw new InvalidOperationException($"MappingGetServices(all) failed: HRESULT 0x{hr:X8}");
        try
        {
            for (int i = 0; i < count; i++)
            {
                MappingServiceInfo s = services[i];
                list.Add(new ServiceSummary(s.guid, Str(s.pszCategory), Str(s.pszDescription),
                    $"{s.wMajorVersion}.{s.wMinorVersion}.{s.wBuildVersion}.{s.wStepVersion}",
                    StrArray(s.prgInputScripts, s.dwInputScriptsCount), StrArray(s.prgOutputScripts, s.dwOutputScriptsCount)));
            }
        }
        finally
        {
            _ = MappingFreeServices(services);
        }
        return list;
    }

    /// <summary>One ELS service opened by GUID.</summary>
    public sealed class Service : IDisposable
    {
        private MappingServiceInfo* _services;

        public Service(Guid guid)
        {
            MappingServiceInfo* services = null;
            uint count = 0;
            var opt = new MappingEnumOptions { Size = (nuint)sizeof(MappingEnumOptions), pGuid = &guid };
            int hr = MappingGetServices(&opt, &services, &count);
            if (hr < 0 || count == 0) throw new InvalidOperationException($"MappingGetServices({guid}) failed: HRESULT 0x{hr:X8}, {count} services");
            _services = services;
        }

        /// <summary>The raw result: per range, the double-NUL-terminated
        /// string list in pData (language detection) or the text in pData
        /// (transliteration), with the range's start and end.</summary>
        public List<(uint Start, uint End, string Data)> Recognize(string text)
        {
            var result = new List<(uint, uint, string)>();
            var bag = new MappingPropertyBag { Size = (nuint)sizeof(MappingPropertyBag) };
            int hr;
            fixed (char* p = text)
            {
                hr = MappingRecognizeText(_services, p, (uint)text.Length, 0, IntPtr.Zero, &bag);
            }
            if (hr < 0) throw new InvalidOperationException($"MappingRecognizeText failed: HRESULT 0x{hr:X8}");
            try
            {
                for (int i = 0; i < bag.dwRangesCount; i++)
                {
                    MappingDataRange r = bag.prgResultRanges[i];
                    string data = r.pData == IntPtr.Zero ? "" : new string((char*)r.pData, 0, (int)(r.dwDataSize / 2));
                    result.Add((r.dwStartIndex, r.dwEndIndex, data));
                }
            }
            finally
            {
                _ = MappingFreePropertyBag(&bag);
            }
            return result;
        }

        /// <summary>Language Detection: the ranked tags of the first range.</summary>
        public List<string> DetectLanguages(string text)
        {
            var ranges = Recognize(text);
            if (ranges.Count == 0) return [];
            return [.. ranges[0].Data.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }

        /// <summary>Transliteration: the concatenated range text, trailing NUL removed.</summary>
        public string Transliterate(string text)
        {
            var ranges = Recognize(text);
            return string.Concat(ranges.Select(r => r.Data.TrimEnd('\0')));
        }

        public void Dispose()
        {
            if (_services != null)
            {
                _ = MappingFreeServices(_services);
                _services = null;
            }
        }
    }
}
