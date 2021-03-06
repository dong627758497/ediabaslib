﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.Zip;

namespace UdsFileReader
{
    public class DataReader
    {
        private static readonly Encoding Encoding = Encoding.GetEncoding(1252);
        public const string FileExtension = ".ldat";
        public const string CodeFileExtension = ".cdat";
        public const string DataDir = "Labels";

        public enum ErrorType
        {
            Iso9141,
            Kwp2000,
            Sae,
            Uds,
        }

        public Dictionary<UInt32, string> CodeMap { get; private set; }

        public enum DataType
        {
            Measurement,
            Basic,
            Adaption,
            Settings,
            Coding,
            LongCoding,
        }

        public class FileNameResolver
        {
            public FileNameResolver(DataReader dataReader, string partNumber, int address)
            {
                DataReader = dataReader;
                PartNumber = partNumber;
                Address = address;

                if (PartNumber.Length > 9)
                {
                    string part1 = PartNumber.Substring(0, 3);
                    string part2 = PartNumber.Substring(3, 3);
                    string part3 = PartNumber.Substring(6, 3);
                    string suffix = PartNumber.Substring(9);
                    _baseName = part1 + "-" + part2 + "-" + part3;
                    _fullName = _baseName + "-" + suffix;
                }
            }

            public string GetFileName(string rootDir)
            {
                try
                {
                    if (string.IsNullOrEmpty(_fullName))
                    {
                        return null;
                    }

                    List<string> dirList = GetDirList(rootDir);
                    string fileName = ResolveFileName(dirList);
                    if (string.IsNullOrEmpty(fileName))
                    {
                        return null;
                    }

                    List<string[]> redirectList = GetRedirects(fileName);
                    if (redirectList != null)
                    {
                        foreach (string[] redirects in redirectList)
                        {
                            string targetFile = Path.ChangeExtension(redirects[0], FileExtension);
                            if (string.IsNullOrEmpty(targetFile))
                            {
                                continue;
                            }

                            for (int i = 1; i < redirects.Length; i++)
                            {
                                string redirect = redirects[i].Trim();
                                bool matched = false;
                                if (redirect.Length > 12)
                                {   // min 1 char suffix
                                    string regString = WildCardToRegular(redirect);
                                    if (Regex.IsMatch(_fullName, regString, RegexOptions.IgnoreCase))
                                    {
                                        matched = true;
                                    }
                                }
                                else
                                {
                                    string fullRedirect = _baseName + redirect;
                                    if (string.Compare(_fullName, fullRedirect, StringComparison.OrdinalIgnoreCase) == 0)
                                    {
                                        matched = true;
                                    }
                                }

                                if (matched)
                                {
                                    foreach (string subDir in dirList)
                                    {
                                        string targetFileName = Path.Combine(subDir, targetFile);
                                        if (File.Exists(targetFileName))
                                        {
                                            return targetFileName;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    return fileName;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private string ResolveFileName(List<string> dirList)
            {
                try
                {
                    foreach (string subDir in dirList)
                    {
                        string fileName = Path.Combine(subDir, _fullName + FileExtension);
                        if (File.Exists(fileName))
                        {
                            return fileName;
                        }

                        fileName = Path.Combine(subDir, _baseName + FileExtension);
                        if (File.Exists(fileName))
                        {
                            return fileName;
                        }
                    }

                    foreach (string subDir in dirList)
                    {
                        string part1 = PartNumber.Substring(0, 2);
                        string part2 = string.Format(CultureInfo.InvariantCulture, "{0:00}", Address);
                        string baseName = part1 + "-" + part2;

                        string fileName = Path.Combine(subDir, baseName + FileExtension);
                        if (File.Exists(fileName))
                        {
                            return fileName;
                        }
                    }
                }
                catch (Exception)
                {
                    return null;
                }

                return null;
            }

            private List<string> GetDirList(string dir)
            {
                try
                {
                    List<string> dirList = new List<string>();
                    string[] dirs = Directory.GetDirectories(dir);
                    if (dirs.Length > 0)
                    {
                        dirList.AddRange(dirs);
                    }

                    dirList.Add(dir);

                    return dirList;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public List<string[]> GetRedirects(string fileName)
            {
                try
                {
                    List<string[]> redirectList = new List<string[]>();
                    List<string[]> textList = ReadFileLines(fileName);
                    foreach (string[] lineArray in textList)
                    {
                        if (lineArray.Length < 3)
                        {
                            continue;
                        }

                        if (string.Compare(lineArray[0], "REDIRECT", StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            continue;
                        }

                        redirectList.Add(lineArray.Skip(1).ToArray());
                    }

                    return redirectList;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public DataReader DataReader { get; }
            public string PartNumber { get; }
            public int Address { get; }

            private readonly string _fullName;
            private readonly string _baseName;
        }

        public List<string> ErrorCodeToString(uint errorCode, uint errorDetail, ErrorType errorType, UdsReader udsReader = null)
        {
            List<string> resultList = new List<string>();
            string errorText = string.Empty;
            uint errorCodeMap = errorCode;
            int errorCodeKey = (int) (errorCode + 100000);
            bool useFullCode = errorCode >= 0x4000 && errorCode <= 0xBFFF;
            if (errorType != ErrorType.Sae)
            {
                useFullCode = false;
                if (errorCode < 0x4000 || errorCode > 0x7FFF)
                {
                    errorCodeKey = (int) errorCode;
                }
                else
                {
                    if (string.IsNullOrEmpty(PcodeToString(errorCode, out uint convertedValue)))
                    {
                        errorCodeKey = -1;
                    }
                    else
                    {
                        errorCodeMap = convertedValue;
                        errorCodeKey = (int) (convertedValue + 100000);
                    }
                }
            }
            bool fullCodeFound = false;
            if (useFullCode)
            {
                uint textKey = (errorCode << 8) | errorDetail;
                if (CodeMap.TryGetValue(textKey, out string longText))
                {
                    errorText = longText;
                    fullCodeFound = true;
                }
            }

            bool splitErrorText = false;
            if (!fullCodeFound)
            {
                if (errorCodeKey >= 0)
                {
                    if (CodeMap.TryGetValue((uint)errorCodeKey, out string shortText))
                    {
                        errorText = shortText;
                        if (errorCodeMap <= 0x3FFF)
                        {
                            splitErrorText = true;
                        }
                        if (errorCodeMap >= 0xC000 && errorCodeMap <= 0xFFFF)
                        {
                            splitErrorText = true;
                        }
                    }
                }
            }

            string errorDetailText1 = string.Empty;
            if (!string.IsNullOrEmpty(errorText))
            {
                int colonIndex = errorText.LastIndexOf(':');
                if (colonIndex >= 0)
                {
                    if (splitErrorText || fullCodeFound)
                    {
                        errorDetailText1 = errorText.Substring(colonIndex + 1).Trim();
                        errorText = errorText.Substring(0, colonIndex);
                    }
                }
            }

            string errorDetailText2 = string.Empty;
            string errorDetailText3 = string.Empty;
            uint detailCode = errorDetail;
            if (!useFullCode)
            {
                switch (errorType)
                {
                    case ErrorType.Iso9141:
                        if ((errorDetail & 0x80) != 0x00)
                        {
                            errorDetailText2 = (UdsReader.GetTextMapText(udsReader, 002693) ?? string.Empty);   // Sporadisch
                        }
                        detailCode &= 0x7F;
                        break;

                    default:
                        if ((errorDetail & 0x60) == 0x20)
                        {
                            errorDetailText2 = (UdsReader.GetTextMapText(udsReader, 002693) ?? string.Empty);   // Sporadisch
                        }
                        if ((errorDetail & 0x80) != 0x00)
                        {
                            errorDetailText3 = (UdsReader.GetTextMapText(udsReader, 066900) ?? string.Empty)
                                               + " " + (UdsReader.GetTextMapText(udsReader, 000085) ?? string.Empty);   // Warnleuchte EIN
                        }
                        detailCode &= 0x0F;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(errorText) && string.IsNullOrEmpty(errorDetailText1))
            {
                uint detailKey = (uint)(detailCode + (useFullCode ? 96000 : 98000));
                if (CodeMap.TryGetValue(detailKey, out string detail))
                {
                    if (!string.IsNullOrEmpty(detail))
                    {
                        errorDetailText1 = detail;
                    }
                }
            }

            if (string.IsNullOrEmpty(errorText))
            {
                errorText = (UdsReader.GetTextMapText(udsReader, 062047) ?? string.Empty); // Unbekannter Fehlercode
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0:000000}", errorCode));
            if (!string.IsNullOrEmpty(errorText))
            {
                sb.Append(" - ");
                sb.Append(errorText);
            }
            resultList.Add(sb.ToString());

            switch (errorType)
            {
                case ErrorType.Iso9141:
                {
                    string detailCodeText = string.Format(CultureInfo.InvariantCulture, "{0:00}-{1}0", detailCode, (errorDetail & 0x80) != 0 ? 1 : 0);
                    if (errorCode > 0x3FFF && errorCode < 65000)
                    {
                        string pcodeText = PcodeToString(errorCode);
                        if (string.IsNullOrEmpty(pcodeText))
                        {
                            pcodeText = (UdsReader.GetTextMapText(udsReader, 099014) ?? string.Empty);  // Unbekannt
                        }
                        resultList.Add(string.Format(CultureInfo.InvariantCulture, "{0} - {1}", pcodeText, detailCodeText));
                    }
                    else
                    {
                        resultList.Add(detailCodeText);
                    }
                    break;
                }

                case ErrorType.Kwp2000:
                    if (errorCode > 0x3FFF && errorCode < 65000)
                    {
                        string pcodeText = PcodeToString(errorCode);
                        if (string.IsNullOrEmpty(pcodeText))
                        {
                            pcodeText = (UdsReader.GetTextMapText(udsReader, 099014) ?? string.Empty);  // Unbekannt
                        }
                        resultList.Add(string.Format(CultureInfo.InvariantCulture, "{0} - {1:000}", pcodeText, detailCode));
                    }
                    else
                    {
                        resultList.Add(string.Format(CultureInfo.InvariantCulture, "{0:000}", detailCode));
                    }
                    break;

                default:
                    resultList.Add(string.Format(CultureInfo.InvariantCulture, "{0} - {1:000}", SaePcodeToString(errorCode), detailCode));
                    break;
            }

            if (!string.IsNullOrEmpty(errorDetailText1))
            {
                resultList.Add(errorDetailText1);
            }
            if (!string.IsNullOrEmpty(errorDetailText2))
            {
                resultList.Add(errorDetailText2);
            }
            if (!string.IsNullOrEmpty(errorDetailText3))
            {
                resultList.Add(errorDetailText3);
            }

            return resultList;
        }

        public List<string> SaeErrorDetailHeadToString(byte[] data, UdsReader udsReader = null)
        {
            List<string> resultList = new List<string>();
            if (data.Length < 15)
            {
                return null;
            }

            if (data[0] != 0x6C)
            {
                return null;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(UdsReader.GetTextMapText(udsReader, 003356) ?? string.Empty);  // Umgebungsbedingungen
            sb.Append(":");
            resultList.Add(sb.ToString());
            UInt32 value = data[2];
            if (value != 0xFF)
            {
                sb.Clear();
                sb.Append(UdsReader.GetTextMapText(udsReader, 018478) ?? string.Empty); // Fehlerstatus
                sb.Append(": ");
                sb.Append(Convert.ToString(value, 2).PadLeft(8, '0'));
                resultList.Add(sb.ToString());
            }

            value = data[3];
            if (value != 0xFF)
            {
                sb.Clear();
                sb.Append(UdsReader.GetTextMapText(udsReader, 016693) ?? string.Empty); // Fehlerpriorität
                sb.Append(": ");
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}", value & 0x0F));
                resultList.Add(sb.ToString());
            }

            value = data[4];
            if (value != 0xFF)
            {
                sb.Clear();
                sb.Append(UdsReader.GetTextMapText(udsReader, 061517) ?? string.Empty); // Fehlerhäufigkeit
                sb.Append(": ");
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}", value));
                resultList.Add(sb.ToString());
            }

            value = data[5];
            if (value != 0xFF)
            {
                sb.Clear();
                sb.Append(UdsReader.GetTextMapText(udsReader, 099026) ?? string.Empty); // Verlernzähler
                sb.Append(": ");
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}", value));
                resultList.Add(sb.ToString());
            }

            value = (UInt32)((data[6] << 16) | (data[7] << 8) | data[8]);
            if (value != 0xFFFFF && value <= 0x3FFFFF)
            {
                sb.Clear();
                sb.Append(UdsReader.GetTextMapText(udsReader, 018858) ?? string.Empty); // Kilometerstand
                sb.Append(": ");
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}", value));
                sb.Append(" ");
                sb.Append(UdsReader.GetUnitMapText(udsReader, 000108) ?? string.Empty); // km
                resultList.Add(sb.ToString());
            }

            value = data[9];
            if (value != 0xFF)
            {
                sb.Clear();
                sb.Append(UdsReader.GetTextMapText(udsReader, 039410) ?? string.Empty); // Zeitangabe
                sb.Append(": ");
                sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}", value & 0x0F));
                resultList.Add(sb.ToString());

                if (value < 2)
                {
                    if (value == 0)
                    {
                        // date time
                        UInt64 timeValue = 0;
                        for (int i = 0; i < 5; i++)
                        {
                            timeValue <<= 8;
                            timeValue += data[10 + i];
                        }
                        if (timeValue != 0 && timeValue != 0x1FFFFFFFF)
                        {
                            UInt64 tempValue = timeValue;
                            UInt64 sec = tempValue & 0x3F;
                            tempValue >>= 6;
                            UInt64 min = tempValue & 0x3F;
                            tempValue >>= 6;
                            UInt64 hour = tempValue & 0x1F;
                            tempValue >>= 5;
                            UInt64 day = tempValue & 0x1F;
                            tempValue >>= 5;
                            UInt64 month = tempValue & 0x0F;
                            tempValue >>= 4;
                            UInt64 year = tempValue & 0x7F;

                            sb.Clear();
                            sb.Append(UdsReader.GetTextMapText(udsReader, 098044) ?? string.Empty); // Datum
                            sb.Append(": ");
                            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0:00}.{1:00}.{2:00}", year + 2000, month, day));
                            resultList.Add(sb.ToString());

                            sb.Clear();
                            sb.Append(UdsReader.GetTextMapText(udsReader, 099068) ?? string.Empty); // Zeit
                            sb.Append(": ");
                            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", hour, min, sec));
                            resultList.Add(sb.ToString());
                        }
                    }
                    else
                    {
                        // life span
                        UInt64 timeValue = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            timeValue <<= 8;
                            timeValue += data[11 + i];
                        }
                        if (timeValue != 0xFFFFFFFF)
                        {
                            sb.Clear();
                            // Zähler Fhzg.-Lebensdauer
                            sb.Append(UdsReader.GetTextMapText(udsReader, 098050) ?? string.Empty);
                            sb.Append(" ");
                            sb.Append(UdsReader.GetTextMapText(udsReader, 047622) ?? string.Empty);
                            sb.Append(": ");
                            sb.Append(string.Format(CultureInfo.InvariantCulture, "{0}", timeValue));
                            resultList.Add(sb.ToString());
                        }
                    }
                }
            }
            return resultList;
        }

        public static string PcodeToString(uint pcodeNum)
        {
            return PcodeToString(pcodeNum, out _);
        }

        public static string PcodeToString(uint pcodeNum, out uint convertedValue)
        {
            convertedValue = 0;
            int codeValue = (int) pcodeNum;
            if (codeValue < 0x4000)
            {
                return string.Empty;
            }
            if (codeValue > 65000)
            {
                return string.Empty;
            }

            int displayCode;
            string codeString;
            if (codeValue < 0x43E8)
            {
                displayCode = codeValue - 0x4000;
            }
            else if (codeValue < 0x3E7 + 0x4400)
            {
                displayCode = codeValue - 0x4018;
            }
            else if (codeValue < 0x3E7 + 0x4800)
            {
                displayCode = codeValue - 0x4030;
            }
            else if (codeValue < 0x3E7 + 0x4C00)
            {
                displayCode = codeValue - 0x4048;
            }
            else
            {
                displayCode = -1;
            }
            if (displayCode >= 0)
            {
                codeString = string.Format(CultureInfo.InvariantCulture, "{0:0000}", displayCode);
                if (!uint.TryParse(codeString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out convertedValue))
                {
                    convertedValue = 0;
                }
                return "P" + codeString;
            }

            if (codeValue < 0x7000)
            {
                return string.Empty;
            }
            if (codeValue > 0x3E7 + 0x7000)
            {
                if (codeValue > 0x3E7 + 0x7400)
                {
                    return string.Empty;
                }
                displayCode = codeValue - 0x7018;
            }
            else
            {
                displayCode = codeValue - 0x7000;
            }
            codeString = string.Format(CultureInfo.InvariantCulture, "{0:0000}", displayCode);
            if (!uint.TryParse(codeString, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out convertedValue))
            {
                convertedValue = 0;
            }
            convertedValue |= 0xC000;
            return "U" + codeString;
        }

        public static string SaePcodeToString(uint pcodeNum)
        {
            char keyLetter = 'P';
            switch ((pcodeNum >> 14) & 0x03)
            {
                case 0x1:
                    keyLetter = 'C';
                    break;

                case 0x2:
                    keyLetter = 'B';
                    break;

                case 0x3:
                    keyLetter = 'U';
                    break;
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}{1:X04}", keyLetter, pcodeNum & 0x3FFF);
        }

        private static string WildCardToRegular(string value)
        {
            return "^" + Regex.Escape(value).Replace("\\?", ".") + "$";
        }

        public static string GetMd5Hash(string text)
        {
            //Prüfen ob Daten übergeben wurden.
            if ((text == null) || (text.Length == 0))
            {
                return string.Empty;
            }

            //MD5 Hash aus dem String berechnen. Dazu muss der string in ein Byte[]
            //zerlegt werden. Danach muss das Resultat wieder zurück in ein string.
            MD5 md5 = new MD5CryptoServiceProvider();
            byte[] textToHash = Encoding.Default.GetBytes(text);
            byte[] result = md5.ComputeHash(textToHash);

            return BitConverter.ToString(result).Replace("-", "");
        }

        public static List<string[]> ReadFileLines(string fileName, bool codeFile = false)
        {
            List<string[]> lineList = new List<string[]>();
            ZipFile zf = null;
            try
            {
                Stream zipStream = null;
                string fileNameBase = Path.GetFileName(fileName);
                FileStream fs = File.OpenRead(fileName);
                zf = new ZipFile(fs)
                {
                    Password = GetMd5Hash(Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant())
                };
                foreach (ZipEntry zipEntry in zf)
                {
                    if (!zipEntry.IsFile)
                    {
                        continue; // Ignore directories
                    }

                    if (string.Compare(zipEntry.Name, fileNameBase, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        zipStream = zf.GetInputStream(zipEntry);
                        break;
                    }
                }

                if (zipStream == null)
                {
                    return null;
                }

                try
                {
                    using (StreamReader sr = new StreamReader(zipStream, Encoding))
                    {
                        for (;;)
                        {
                            string line = sr.ReadLine();
                            if (line == null)
                            {
                                break;
                            }

                            if (codeFile)
                            {
                                string[] lineArray = line.Split(new [] {','}, 2);
                                if (lineArray.Length > 0)
                                {
                                    lineList.Add(lineArray);
                                }
                            }
                            else
                            {
                                int commentStart = line.IndexOf(';');
                                if (commentStart >= 0)
                                {
                                    line = line.Substring(0, commentStart);
                                }

                                if (string.IsNullOrWhiteSpace(line))
                                {
                                    continue;
                                }

                                string[] lineArray = line.Split(',');
                                if (lineArray.Length > 0)
                                {
                                    lineList.Add(lineArray);
                                }
                            }
                        }
                    }
                }
                catch (NotImplementedException)
                {
                    // closing of encrypted stream throws execption
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (zf != null)
                {
                    zf.IsStreamOwner = true; // Makes close also shut the underlying stream
                    zf.Close(); // Ensure we release resources
                }
            }

            return lineList;
        }

        public class DataInfo
        {
            public DataInfo(DataType dataType, int? value1, int? value2, string[] textArray)
            {
                DataType = dataType;
                Value1 = value1;
                Value2 = value2;
                TextArray = textArray;
            }

            public DataType DataType { get; }
            public int? Value1 { get; }
            public int? Value2 { get; }
            public string[] TextArray { get; }
        }

        public List<DataInfo> ExtractDataType(string fileName, DataType dataType)
        {
            try
            {
                List<DataInfo> dataInfoList = new List<DataInfo>();
                List<string[]> lineList = ReadFileLines(fileName);
                if (lineList == null)
                {
                    return null;
                }

                char? prefix = null;
                int numberCount = 2;
                int textOffset = 2;
                switch (dataType)
                {
                    case DataType.Adaption:
                        prefix = 'A';
                        break;

                    case DataType.Basic:
                        prefix = 'B';
                        break;

                    case DataType.Settings:
                        prefix = 'S';
                        textOffset = 1;
                        numberCount = 1;
                        break;

                    case DataType.Coding:
                        prefix = 'C';
                        textOffset = 1;
                        numberCount = 1;
                        break;

                    case DataType.LongCoding:
                        prefix = 'L';
                        textOffset = 1;
                        numberCount = 0;
                        break;
                }

                foreach (string[] lineArray in lineList)
                {
                    if (lineArray.Length < 2)
                    {
                        continue;
                    }

                    string entry1 = lineArray[0].Trim();
                    if (entry1.Length < 1)
                    {
                        continue;
                    }

                    if (prefix != null)
                    {
                        if (entry1[0] != prefix)
                        {
                            continue;
                        }

                        entry1 = entry1.Substring(1);
                    }
                    else
                    {
                        if (!Char.IsNumber(entry1[0]))
                        {
                            continue;
                        }
                    }

                    int? value1 = null;
                    int? value2 = null;
                    if (numberCount >= 1)
                    {
                        if (Int32.TryParse(entry1, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out Int32 valueOut1))
                        {
                            value1 = valueOut1;
                        }
                    }
                    if (numberCount >= 2)
                    {
                        if (Int32.TryParse(lineArray[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 valueOut2))
                        {
                            value2 = valueOut2;
                        }
                    }
                    string[] textArray = lineArray.Skip(textOffset).ToArray();

                    dataInfoList.Add(new DataInfo(dataType, value1, value2, textArray));
                }

                return dataInfoList;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool Init(string rootDir)
        {
            try
            {
                string[] files = Directory.GetFiles(rootDir, "Code*" + CodeFileExtension, SearchOption.TopDirectoryOnly);
                if (files.Length != 1)
                {
                    return false;
                }
                List<string[]> lineList = ReadFileLines(files[0], true);
                if (lineList == null)
                {
                    return false;
                }
                CodeMap = new Dictionary<uint, string>();
                foreach (string[] lineArray in lineList)
                {
                    if (lineArray.Length != 2)
                    {
                        return false;
                    }
                    if (!UInt32.TryParse(lineArray[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt32 key))
                    {
                        return false;
                    }
                    CodeMap.Add(key, lineArray[1]);
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
