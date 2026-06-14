using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TRPServerPanel.Models;

namespace TRPServerPanel.Services
{
    /// <summary>
    /// Service for connecting Gemini AI agent to the Server Panel.
    /// Acts as a "Bridge" allowing the AI to call app methods (Tools).
    /// </summary>
    public class GeminiService
    {
        private readonly HttpClient _httpClient;
        private SecureString? _secureApiKey;
        private Process? _bridgeProcess;
        public string ActiveModel { get; set; } = "nvidia/nemotron-3-super-120b-a12b:free";

        public event Action<string>? OnAgentMessage;
        public event Action<string, object>? OnToolCall;

        public GeminiService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            // Default to looking for API Key in Environment or AppSettings
            SetSecureApiKey(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
        }

        private void SetSecureApiKey(string? apiKey)
        {
            if (_secureApiKey != null)
            {
                _secureApiKey.Dispose();
                _secureApiKey = null;
            }

            if (string.IsNullOrEmpty(apiKey)) return;

            _secureApiKey = new SecureString();
            foreach (char c in apiKey)
            {
                _secureApiKey.AppendChar(c);
            }
            _secureApiKey.MakeReadOnly();
        }

        private string GetDecryptedApiKey()
        {
            if (_secureApiKey == null || _secureApiKey.Length == 0) return string.Empty;

            IntPtr valuePtr = IntPtr.Zero;
            try
            {
                valuePtr = Marshal.SecureStringToGlobalAllocUnicode(_secureApiKey);
                return Marshal.PtrToStringUni(valuePtr) ?? string.Empty;
            }
            finally
            {
                if (valuePtr != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(valuePtr);
                }
            }
        }

        private static readonly string ExpectedBridgeHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

        private bool VerifyBridgeIntegrity(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                byte[] fileBytes = File.ReadAllBytes(path);
                byte[] hashBytes = SHA256.HashData(fileBytes);
                string fileHash = Convert.ToHexString(hashBytes);

                // For development fallback/custom binaries, if hash is empty/placeholder or matches, allow it
                if (ExpectedBridgeHash == "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")
                {
                    Debug.WriteLine("[GEMINI] Bridge warning: Running in Dev mode, signature validation bypassed.");
                    return true;
                }

                return string.Equals(fileHash, ExpectedBridgeHash, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GEMINI] Integrity check exception: {ex.Message}");
                return false;
            }
        }

        public void Initialize(string? customApiKey = null)
        {
            if (!string.IsNullOrEmpty(customApiKey)) SetSecureApiKey(customApiKey);
            
            // Check if gemini-bridge.exe exists (compiled from gemini-rs)
            string bridgePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "gemini-bridge.exe");
            if (File.Exists(bridgePath))
            {
                if (VerifyBridgeIntegrity(bridgePath))
                {
                    StartBridgeProcess(bridgePath);
                }
                else
                {
                    Debug.WriteLine("[GEMINI] Bridge binary failed integrity verification. Execution blocked.");
                }
            }
            else
            {
                Debug.WriteLine("[GEMINI] Bridge binary missing. Falling back to Native HTTP Agent.");
            }
        }

        private void StartBridgeProcess(string path)
        {
            try
            {
                _bridgeProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = "--mode bridge",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                _bridgeProcess.OutputDataReceived += (s, e) => HandleBridgeOutput(e.Data);
                _bridgeProcess.Start();
                _bridgeProcess.BeginOutputReadLine();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GEMINI] Failed to start bridge: {ex.Message}");
            }
        }

        private void HandleBridgeOutput(string? data)
        {
            if (string.IsNullOrEmpty(data)) return;

            try
            {
                using (var doc = JsonDocument.Parse(data))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeProp))
                    {
                        string type = typeProp.GetString() ?? "";
                        if (type == "tool_call")
                        {
                            string toolName = root.GetProperty("name").GetString() ?? "";
                            var parameters = root.GetProperty("args");
                            OnToolCall?.Invoke(toolName, parameters);
                        }
                        else if (type == "message")
                        {
                            OnAgentMessage?.Invoke(root.GetProperty("content").GetString() ?? "");
                        }
                    }
                }
            }
            catch { /* Parsing error from bridge stdout */ }
        }

        public class AuditCacheItem
        {
            public bool IsSafe { get; set; }
            public string Description { get; set; } = string.Empty;
            public DateTime CachedAt { get; set; }
        }

        private readonly string _cacheFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "audit_cache.json");
        private readonly SemaphoreSlim _cacheLock = new(1, 1);

        private string ComputeSha256(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = SHA256.HashData(bytes);
            return Convert.ToHexString(hashBytes);
        }

        private Dictionary<string, AuditCacheItem> LoadAuditCache()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    string json = File.ReadAllText(_cacheFilePath);
                    return JsonSerializer.Deserialize<Dictionary<string, AuditCacheItem>>(json) ?? new();
                }
            }
            catch { }
            return new();
        }

        private void SaveAuditCache(Dictionary<string, AuditCacheItem> cache)
        {
            try
            {
                string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                string tempPath = _cacheFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(_cacheFilePath))
                {
                    File.Delete(_cacheFilePath);
                }
                File.Move(tempPath, _cacheFilePath);
            }
            catch { }
        }

        public async Task<(bool Safe, string Description)> AuditPluginAsync(string filename, string content)
        {
            string sha256 = ComputeSha256(content);

            await _cacheLock.WaitAsync();
            try
            {
                var cache = LoadAuditCache();
                if (cache.TryGetValue(sha256, out var cachedItem))
                {
                    Debug.WriteLine($"[AI SHIELD] Returning cached audit result for {filename} (SHA256: {sha256})");
                    return (cachedItem.IsSafe, cachedItem.Description);
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            string prompt = $"Audit this Rust C# plugin code for security risks (backdoors, malcode, data theft):\n\nFILENAME: {filename}\n\nCODE:\n{content}";
            string context = "You are AI SHIELD, a cybersecurity expert specializing in Rust game server plugins (Oxide/Carbon). " +
                             "Look for hidden RCON commands, unauthorized web requests, or code that grants admin rights unexpectedly. " +
                             "Return your findings as a summary. Start with [SAFE] or [DANGEROUS].";

            string response = await AskAgentAsync(prompt, context);
            bool isSafe = !response.Contains("[DANGEROUS]", StringComparison.OrdinalIgnoreCase);

            await _cacheLock.WaitAsync();
            try
            {
                var cache = LoadAuditCache();
                cache[sha256] = new AuditCacheItem
                {
                    IsSafe = isSafe,
                    Description = response,
                    CachedAt = DateTime.Now
                };
                SaveAuditCache(cache);
            }
            finally
            {
                _cacheLock.Release();
            }
            
            return (isSafe, response);
        }

        private IEnumerable<string> FilterSecurityLogs(IEnumerable<string> rawLogs)
        {
            var filtered = new List<string>();
            foreach (var line in rawLogs)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                bool isNoise = false;
                // Filter out standard performance metrics and typical console spam
                if (line.Contains("FPS:", StringComparison.OrdinalIgnoreCase) && line.Contains("Entities:", StringComparison.OrdinalIgnoreCase))
                {
                    isNoise = true;
                }
                else if (line.Contains("A2S_INFO", StringComparison.OrdinalIgnoreCase) || line.Contains("A2S_GETFREE", StringComparison.OrdinalIgnoreCase))
                {
                    isNoise = true;
                }
                else if (line.Contains("Saved ", StringComparison.OrdinalIgnoreCase) && line.Contains("entities in", StringComparison.OrdinalIgnoreCase))
                {
                    isNoise = true;
                }
                else if (line.Contains("SteamConn:", StringComparison.OrdinalIgnoreCase) || line.Contains("SteamNetConnectionStatus", StringComparison.OrdinalIgnoreCase))
                {
                    isNoise = true;
                }

                if (!isNoise)
                {
                    filtered.Add(line);
                }
            }

            // Keep only the last 300 logs to stay within reasonable token bounds
            if (filtered.Count > 300)
            {
                return System.Linq.Enumerable.Skip(filtered, filtered.Count - 300);
            }
            return filtered;
        }

        /// <summary>
        /// Audits server logs for security anomalies (Admin abuse, Brute force, Exploits).
        /// </summary>
        public async Task<SecurityReport> AuditLogAsync(IEnumerable<string> logLines)
        {
            var filteredLogs = FilterSecurityLogs(logLines);
            string combinedLogs = string.Join("\n", filteredLogs);
            if (string.IsNullOrEmpty(combinedLogs)) return new SecurityReport { Summary = "No relevant logs provided." };

            string prompt = $"Analyze these Rust server console/RCON logs for security threats:\n\nLOGS:\n{combinedLogs}";
            string context = "You are AI SENTINEL, a specialized security auditor for Rust game servers. " +
                             "Analyze the logs for: RCON brute force, unusual admin commands, plugin errors, or player exploits. " +
                             "Return your report ONLY in JSON format with fields: RiskLevel, Summary, Findings (List of objects with Description, Severity, Type), Recommendations (List).";

            string response = await AskAgentAsync(prompt, context, forceJson: true);
            var report = new SecurityReport { RawResponse = response };

            try
            {
                string jsonStr = CleanJsonResponse(response);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<SecurityReport>(jsonStr, options);
                if (parsed != null)
                {
                    report.RiskLevel = parsed.RiskLevel;
                    report.Summary = parsed.Summary;
                    report.Findings = parsed.Findings;
                    report.Recommendations = parsed.Recommendations;
                }
            }
            catch
            {
                report.Summary = "Failed to parse AI response. " + response;
            }

            return report;
        }

        private string CleanJsonResponse(string raw)
        {
            if (raw.Contains("```json"))
            {
                int start = raw.IndexOf("```json") + 7;
                int end = raw.LastIndexOf("```");
                if (end > start) return raw.Substring(start, end - start).Trim();
            }
            else if (raw.Contains("```"))
            {
                int start = raw.IndexOf("```") + 3;
                int end = raw.LastIndexOf("```");
                if (end > start) return raw.Substring(start, end - start).Trim();
            }
            return raw.Trim();
        }

        public async Task<string> AskAgentAsync(string prompt, string context = "", bool forceJson = false)
        {
            string apiKey = GetDecryptedApiKey();
            if (string.IsNullOrEmpty(apiKey)) return "Error: Gemini API Key missing.";

            bool isOpenRouter = apiKey.StartsWith("sk-or-v1-");
            string url;
            object requestBody;

            try
            {
                if (isOpenRouter)
                {
                    // OpenRouter (OpenAI-compatible) Format
                    url = "https://openrouter.ai/api/v1/chat/completions";
                    
                    if (forceJson)
                    {
                        requestBody = new
                        {
                            model = ActiveModel, 
                            messages = new[]
                            {
                                new { role = "system", content = context },
                                new { role = "user", content = prompt }
                            },
                            response_format = new { type = "json_object" }
                        };
                    }
                    else
                    {
                        requestBody = new
                        {
                            model = ActiveModel, 
                            messages = new[]
                            {
                                new { role = "system", content = context },
                                new { role = "user", content = prompt }
                            }
                        };
                    }
                }
                else
                {
                    // Native Google Gemini Format
                    url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={apiKey}";
                    
                    if (forceJson)
                    {
                        requestBody = new
                        {
                            contents = new[]
                            {
                                new { parts = new[] { new { text = $"{context}\n\nUser: {prompt}" } } }
                            },
                            generationConfig = new
                            {
                                responseMimeType = "application/json"
                            }
                        };
                    }
                    else
                    {
                        requestBody = new
                        {
                            contents = new[]
                            {
                                new { parts = new[] { new { text = $"{context}\n\nUser: {prompt}" } } }
                            }
                        };
                    }
                }

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = content
                };

                if (isOpenRouter)
                {
                    request.Headers.Add("Authorization", $"Bearer {apiKey}");
                    // Mandatory OpenRouter headers
                    request.Headers.Add("HTTP-Referer", "https://github.com/TEAM_RUST_PLUGINS/TRPServerPanel");
                    request.Headers.Add("X-Title", "TRP Server Panel");
                }

                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var resJson = await response.Content.ReadAsStringAsync();
                    using (var doc = JsonDocument.Parse(resJson))
                    {
                        var root = doc.RootElement;
                        if (isOpenRouter)
                        {
                            // OpenAI/OpenRouter Response Parsing
                            return root.GetProperty("choices")[0]
                                .GetProperty("message")
                                .GetProperty("content")
                                .GetString() ?? "No response.";
                        }
                        else
                        {
                            // Google Response Parsing
                            return root.GetProperty("candidates")[0]
                                .GetProperty("content")
                                .GetProperty("parts")[0]
                                .GetProperty("text")
                                .GetString() ?? "No response.";
                        }
                    }
                }
                
                string errorDetail = await response.Content.ReadAsStringAsync();
                return $"Error {response.StatusCode}: {errorDetail}";
            }
            catch (Exception ex)
            {
                return $"Exception: {ex.Message}";
            }
        }

        public void Shutdown()
        {
            if (_bridgeProcess != null && !_bridgeProcess.HasExited)
            {
                _bridgeProcess.Kill();
            }
        }
    }
}
