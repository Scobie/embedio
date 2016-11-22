﻿#if !NET46
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace Unosquare.Net
{
    // We don't use the cooked URL because http.sys unescapes all percent-encoded values. However,
    // we also can't just use the raw Uri, since http.sys supports not only Utf-8, but also ANSI/DBCS and
    // Unicode code points. System.Uri only supports Utf-8.
    // The purpose of this class is to convert all ANSI, DBCS, and Unicode code points into percent encoded
    // Utf-8 characters.
    internal sealed class HttpListenerRequestUriBuilder
    {
        private static readonly bool UseCookedRequestUrl = false;
        private static readonly Encoding Utf8Encoding;
        private static readonly Encoding AnsiEncoding;

        private readonly string _rawUri;
        private readonly string _cookedUriScheme;
        private readonly string _cookedUriHost;
        private readonly string _cookedUriPath;
        private readonly string _cookedUriQuery;

        // This field is used to build the final request Uri string from the Uri parts passed to the ctor.
        private StringBuilder _requestUriString;

        // The raw path is parsed by looping through all characters from left to right. 'rawOctets'
        // is used to store consecutive percent encoded octets as actual byte values: e.g. for path /pa%C3%84th%2F/
        // rawOctets will be set to { 0xC3, 0x84 } when we reach character 't' and it will be { 0x2F } when
        // we reach the final '/'. I.e. after a sequence of percent encoded octets ends, we use rawOctets as 
        // input to the encoding and percent encode the resulting string into UTF-8 octets.
        //
        // When parsing ANSI (Latin 1) encoded path '/pa%C4th/', %C4 will be added to rawOctets and when
        // we reach 't', the content of rawOctets { 0xC4 } will be fed into the ANSI encoding. The resulting 
        // string '�' will be percent encoded into UTF-8 octets and appended to requestUriString. The final
        // path will be '/pa%C3%84th/', where '%C3%84' is the UTF-8 percent encoded character '�'.
        private List<byte> _rawOctets;
        private string _rawPath;

        // Holds the final request Uri.
        private Uri _requestUri;

        static HttpListenerRequestUriBuilder()
        {
            //useCookedRequestUrl = SettingsSectionInternal.Section.HttpListenerUnescapeRequestUrl;
            Utf8Encoding = new UTF8Encoding(false, true);
            AnsiEncoding = Encoding.GetEncoding(0, new EncoderExceptionFallback(), new DecoderExceptionFallback());
        }

        private HttpListenerRequestUriBuilder(string rawUri, string cookedUriScheme, string cookedUriHost,
            string cookedUriPath, string cookedUriQuery)
        {
            Debug.Assert(!string.IsNullOrEmpty(rawUri), "Empty raw URL.");
            Debug.Assert(!string.IsNullOrEmpty(cookedUriScheme), "Empty cooked URL scheme.");
            Debug.Assert(!string.IsNullOrEmpty(cookedUriHost), "Empty cooked URL host.");
            Debug.Assert(!string.IsNullOrEmpty(cookedUriPath), "Empty cooked URL path.");

            _rawUri = rawUri;
            _cookedUriScheme = cookedUriScheme;
            _cookedUriHost = cookedUriHost;
            _cookedUriPath = AddSlashToAsteriskOnlyPath(cookedUriPath);

            _cookedUriQuery = cookedUriQuery ?? string.Empty;
        }

        public static Uri GetRequestUri(string rawUri, string cookedUriScheme, string cookedUriHost,
            string cookedUriPath, string cookedUriQuery)
        {
            var builder = new HttpListenerRequestUriBuilder(rawUri,
                cookedUriScheme, cookedUriHost, cookedUriPath, cookedUriQuery);

            return builder.Build();
        }

        private Uri Build()
        {
            // if the user enabled the "use raw Uri" setting in <httpListener> section, we'll use the raw
            // path rather than the cooked path.
            if (UseCookedRequestUrl)
            {
                // corresponds to pre-4.0 behavior: use the cooked URI.
                BuildRequestUriUsingCookedPath();

                if (_requestUri == null)
                {
                    BuildRequestUriUsingRawPath();
                }
            }
            else
            {
                BuildRequestUriUsingRawPath();

                if (_requestUri == null)
                {
                    BuildRequestUriUsingCookedPath();
                }
            }

            return _requestUri;
        }

        private void BuildRequestUriUsingCookedPath()
        {
            var isValid =
                Uri.TryCreate(_cookedUriScheme + Extensions.SchemeDelimiter + _cookedUriHost + _cookedUriPath +
                              _cookedUriQuery, UriKind.Absolute, out _requestUri);

            // Creating a Uri from the cooked Uri should really always work: If not, we log at least.
            if (!isValid)
            {
                //LogWarning("BuildRequestUriUsingCookedPath", SR.net_log_listener_cant_create_uri, cookedUriScheme,
                //    cookedUriHost, cookedUriPath, cookedUriQuery);
            }
        }

        private void BuildRequestUriUsingRawPath()
        {
            var isValid = false;

            // Initialize 'rawPath' only if really needed; i.e. if we build the request Uri from the raw Uri.
            _rawPath = GetPath(_rawUri);

            // If HTTP.sys only parses Utf-8, we can safely use the raw path: it must be a valid Utf-8 string.
            if (!HttpSysSettings.EnableNonUtf8 || (_rawPath == string.Empty))
            {
                var path = _rawPath;
                if (path == string.Empty)
                {
                    path = "/";
                    Debug.Assert(_cookedUriQuery == string.Empty,
                        "Query is only allowed if there is a non-empty path. At least '/' path required.");
                }

                isValid =
                    Uri.TryCreate(
                        _cookedUriScheme + Extensions.SchemeDelimiter + _cookedUriHost + path + _cookedUriQuery,
                        UriKind.Absolute, out _requestUri);
            }
            else
            {
                // Try to check the raw path using first the primary encoding (according to http.sys settings);
                // if it fails try the secondary encoding.
                var result = BuildRequestUriUsingRawPath(GetEncoding(EncodingType.Primary));
                if (result == ParsingResult.EncodingError)
                {
                    var secondaryEncoding = GetEncoding(EncodingType.Secondary);
                    result = BuildRequestUriUsingRawPath(secondaryEncoding);
                }
                isValid = (result == ParsingResult.Success) ? true : false;
            }

            // Log that we weren't able to create a Uri from the raw string.
            if (!isValid)
            {
                //LogWarning("BuildRequestUriUsingRawPath", SR.net_log_listener_cant_create_uri, cookedUriScheme,
                //    cookedUriHost, rawPath, cookedUriQuery);
            }
        }

        private static Encoding GetEncoding(EncodingType type)
        {
            //Debug.Assert(HttpSysSettings.EnableNonUtf8,
            //    "If 'EnableNonUtf8' is false we shouldn't require an encoding. It's always Utf-8.");
            //Debug.Assert((type == EncodingType.Primary) || (type == EncodingType.Secondary),
            //    "Unknown 'EncodingType' value: " + type.ToString());

            if (((type == EncodingType.Primary) && (!HttpSysSettings.FavorUtf8)) ||
                ((type == EncodingType.Secondary) && (HttpSysSettings.FavorUtf8)))
            {
                return AnsiEncoding;
            }
            return Utf8Encoding;
        }

        private ParsingResult BuildRequestUriUsingRawPath(Encoding encoding)
        {
            Debug.Assert(encoding != null, "'encoding' must be assigned.");
            Debug.Assert(!string.IsNullOrEmpty(_rawPath), "'rawPath' must have at least one character.");

            _rawOctets = new List<byte>();
            _requestUriString = new StringBuilder();
            _requestUriString.Append(_cookedUriScheme);
            _requestUriString.Append(Extensions.SchemeDelimiter);
            _requestUriString.Append(_cookedUriHost);

            var result = ParseRawPath(encoding);
            if (result == ParsingResult.Success)
            {
                _requestUriString.Append(_cookedUriQuery);

                Debug.Assert(_rawOctets.Count == 0,
                    "Still raw octets left. They must be added to the result path.");

                if (!Uri.TryCreate(_requestUriString.ToString(), UriKind.Absolute, out _requestUri))
                {
                    // If we can't create a Uri from the string, this is an invalid string and it doesn't make 
                    // sense to try another encoding.
                    result = ParsingResult.InvalidString;
                }
            }

            if (result != ParsingResult.Success)
            {
                //LogWarning("BuildRequestUriUsingRawPath", SR.net_log_listener_cant_convert_raw_path, rawPath,
                //    encoding.EncodingName);
            }

            return result;
        }

        private ParsingResult ParseRawPath(Encoding encoding)
        {
            Debug.Assert(encoding != null, "'encoding' must be assigned.");

            var index = 0;
            while (index < _rawPath.Length)
            {
                var current = _rawPath[index];
                if (current == '%')
                {
                    // Assert is enough, since http.sys accepted the request string already. This should never happen.
                    Debug.Assert(index + 2 < _rawPath.Length, "Expected >=2 characters after '%' (e.g. %2F)");

                    index++;
                    current = _rawPath[index];
                    if (current == 'u' || current == 'U')
                    {
                        // We found "%u" which means, we have a Unicode code point of the form "%uXXXX".
                        Debug.Assert(index + 4 < _rawPath.Length, "Expected >=4 characters after '%u' (e.g. %u0062)");

                        // Decode the content of rawOctets into percent encoded UTF-8 characters and append them
                        // to requestUriString.
                        if (!EmptyDecodeAndAppendRawOctetsList(encoding))
                        {
                            return ParsingResult.EncodingError;
                        }
                        if (!AppendUnicodeCodePointValuePercentEncoded(_rawPath.Substring(index + 1, 4)))
                        {
                            return ParsingResult.InvalidString;
                        }
                        index += 5;
                    }
                    else
                    {
                        // We found '%', but not followed by 'u', i.e. we have a percent encoded octed: %XX 
                        if (!AddPercentEncodedOctetToRawOctetsList(encoding, _rawPath.Substring(index, 2)))
                        {
                            return ParsingResult.InvalidString;
                        }
                        index += 2;
                    }
                }
                else
                {
                    // We found a non-'%' character: decode the content of rawOctets into percent encoded
                    // UTF-8 characters and append it to the result. 
                    if (!EmptyDecodeAndAppendRawOctetsList(encoding))
                    {
                        return ParsingResult.EncodingError;
                    }
                    // Append the current character to the result.
                    _requestUriString.Append(current);
                    index++;
                }
            }

            // if the raw path ends with a sequence of percent encoded octets, make sure those get added to the
            // result (requestUriString).
            return !EmptyDecodeAndAppendRawOctetsList(encoding) ? ParsingResult.EncodingError : ParsingResult.Success;
        }

        private bool AppendUnicodeCodePointValuePercentEncoded(string codePoint)
        {
            // http.sys only supports %uXXXX (4 hex-digits), even though unicode code points could have up to
            // 6 hex digits. Therefore we parse always 4 characters after %u and convert them to an int.
            int codePointValue;
            if (!int.TryParse(codePoint, NumberStyles.HexNumber, null, out codePointValue))
            {
                //LogWarning("AppendUnicodeCodePointValuePercentEncoded",
                //    SR.net_log_listener_cant_convert_percent_value, codePoint);
                return false;
            }

            try
            {
                var unicodeString = char.ConvertFromUtf32(codePointValue);
                AppendOctetsPercentEncoded(_requestUriString, Utf8Encoding.GetBytes(unicodeString));

                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                //LogWarning("AppendUnicodeCodePointValuePercentEncoded", SR.net_log_listener_cant_convert_percent_value,
                //    codePoint);
            }
            catch (EncoderFallbackException)
            {
                // If utf8Encoding.GetBytes() fails
                //LogWarning("AppendUnicodeCodePointValuePercentEncoded", SR.net_log_listener_cant_convert_to_utf8,
                //    unicodeString, e.Message);
            }

            return false;
        }

        private bool AddPercentEncodedOctetToRawOctetsList(Encoding encoding, string escapedCharacter)
        {
            byte encodedValue;
            if (!byte.TryParse(escapedCharacter, NumberStyles.HexNumber, null, out encodedValue))
            {
                //LogWarning("AddPercentEncodedOctetToRawOctetsList", SR.net_log_listener_cant_convert_percent_value,
                //    escapedCharacter);
                return false;
            }

            _rawOctets.Add(encodedValue);

            return true;
        }

        private bool EmptyDecodeAndAppendRawOctetsList(Encoding encoding)
        {
            if (_rawOctets.Count == 0)
            {
                return true;
            }

            try
            {
                // If the encoding can get a string out of the byte array, this is a valid string in the
                // 'encoding' encoding.
                var decodedString = encoding.GetString(_rawOctets.ToArray());

                AppendOctetsPercentEncoded(_requestUriString,
                    encoding == Utf8Encoding ? _rawOctets.ToArray() : Utf8Encoding.GetBytes(decodedString));

                _rawOctets.Clear();

                return true;
            }
            catch (DecoderFallbackException)
            {
                //LogWarning("EmptyDecodeAndAppendRawOctetsList", SR.net_log_listener_cant_convert_bytes,
                //    GetOctetsAsString(rawOctets), e.Message);
            }
            catch (EncoderFallbackException)
            {
                // If utf8Encoding.GetBytes() fails
                //LogWarning("EmptyDecodeAndAppendRawOctetsList", SR.net_log_listener_cant_convert_to_utf8,
                //    decodedString, e.Message);
            }

            return false;
        }

        private static void AppendOctetsPercentEncoded(StringBuilder target, IEnumerable<byte> octets)
        {
            foreach (var octet in octets)
            {
                target.Append('%');
                target.Append(octet.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        private static string GetOctetsAsString(IEnumerable<byte> octets)
        {
            var octetString = new StringBuilder();

            var first = true;
            foreach (var octet in octets)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    octetString.Append(" ");
                }
                octetString.Append(octet.ToString("X2", CultureInfo.InvariantCulture));
            }

            return octetString.ToString();
        }

        private static string GetPath(string uriString)
        {
            Debug.Assert(uriString != null, "uriString must not be null");
            Debug.Assert(uriString.Length > 0, "uriString must not be empty");

            var pathStartIndex = 0;

            // Perf. improvement: nearly all strings are relative Uris. So just look if the
            // string starts with '/'. If so, we have a relative Uri and the path starts at position 0.
            // (http.sys already trimmed leading whitespaces)
            if (uriString[0] != '/')
            {
                // We can't check against cookedUriScheme, since http.sys allows for request http://myserver/ to
                // use a request line 'GET https://myserver/' (note http vs. https). Therefore check if the
                // Uri starts with either http:// or https://.
                var authorityStartIndex = 0;
                if (uriString.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    authorityStartIndex = 7;
                }
                else if (uriString.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    authorityStartIndex = 8;
                }

                if (authorityStartIndex > 0)
                {
                    // we have an absolute Uri. Find out where the authority ends and the path begins.
                    // Note that Uris like "http://server?query=value/1/2" are invalid according to RFC2616
                    // and http.sys behavior: If the Uri contains a query, there must be at least one '/'
                    // between the authority and the '?' character: It's safe to just look for the first
                    // '/' after the authority to determine the beginning of the path.
                    pathStartIndex = uriString.IndexOf('/', authorityStartIndex);
                    if (pathStartIndex == -1)
                    {
                        // e.g. for request lines like: 'GET http://myserver' (no final '/')
                        pathStartIndex = uriString.Length;
                    }
                }
                else
                {
                    // RFC2616: Request-URI = "*" | absoluteURI | abs_path | authority
                    // 'authority' can only be used with CONNECT which is never received by HttpListener.
                    // I.e. if we don't have an absolute path (must start with '/') and we don't have
                    // an absolute Uri (must start with http:// or https://), then 'uriString' must be '*'.
                    Debug.Assert((uriString.Length == 1) && (uriString[0] == '*'), "Unknown request Uri string format",
                        "Request Uri string is not an absolute Uri, absolute path, or '*': {0}", uriString);

                    // Should we ever get here, be consistent with 2.0/3.5 behavior: just add an initial
                    // slash to the string and treat it as a path:
                    uriString = "/" + uriString;
                }
            }

            // Find end of path: The path is terminated by
            // - the first '?' character
            // - the first '#' character: This is never the case here, since http.sys won't accept 
            //   Uris containing fragments. Also, RFC2616 doesn't allow fragments in request Uris.
            // - end of Uri string
            var queryIndex = uriString.IndexOf('?');
            if (queryIndex == -1)
            {
                queryIndex = uriString.Length;
            }

            // will always return a != null string.
            return AddSlashToAsteriskOnlyPath(uriString.Substring(pathStartIndex, queryIndex - pathStartIndex));
        }

        private static string AddSlashToAsteriskOnlyPath(string path)
        {
            Debug.Assert(path != null, "'path' must not be null");

            // If a request like "OPTIONS * HTTP/1.1" is sent to the listener, then the request Uri
            // should be "http[s]://server[:port]/*" to be compatible with pre-4.0 behavior.
            if ((path.Length == 1) && (path[0] == '*'))
            {
                return "/*";
            }

            return path;
        }

        private enum ParsingResult
        {
            Success,
            InvalidString,
            EncodingError
        }

        private enum EncodingType
        {
            Primary,
            Secondary
        }
    }
}

#endif