﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Configuration;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Newtonsoft.Json.Linq;

namespace AADHelpers
{
    public static class TokenUtils
    {
        static string[] AADLoginUrls = new[]
        {
            "https://login.windows-ppe.net",
            "https://login.windows-ppe.net",
            "https://login.windows-ppe.net",
            "https://login.windows.net"
        };

        private static string[] AADGraphUrls = new[]
        {
            "https://graph.ppe.windows.net",
            "https://graph.ppe.windows.net",
            "https://graph.ppe.windows.net",
            "https://graph.windows.net"
        };

        static string[] CSMUrls = new[]
        {
            "https://api-next.resources.windows-int.net",
            "https://api-current.resources.windows-int.net",
            "https://api-dogfood.resources.windows-int.net",
            "https://management.azure.com"
        };

        static string[] InfrastructureTenantIds = new[]
        {
            "ea8a4392-515e-481f-879e-6571ff2a8a36",
            "f8cdef31-a31e-4b4a-93e4-5f571e91255a"
        };

        public const string AzureToolClientId = "1950a258-227b-4e31-a9cf-717495945fc2";
        public const string CSMApiVersion = "2014-01-01";
        public const string AADGraphApiVersion = "1.5";

        private static string _aadTenantId;
        private static string _aadClientId;
        private static string _aadRedirectUri;
        private static Lazy<AzureEnvs> _env = new Lazy<AzureEnvs>(() => 
        {
            var file = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".csm"), "recent_env.txt");
            return (AzureEnvs)Enum.Parse(typeof(AzureEnvs), File.ReadAllText(file));
        });

        public static string AADTenantId 
        { 
            get { return _aadTenantId ?? (_aadTenantId = ConfigurationManager.AppSettings["AADTenantId"]); } 
        }

        public static string AADClientId 
        { 
            get { return _aadClientId ?? (_aadClientId = ConfigurationManager.AppSettings["AADClientId"]); } 
        }

        public static string AADRedirectUri
        { 
            get { return _aadRedirectUri ?? (_aadRedirectUri = ConfigurationManager.AppSettings["AADRedirectUri"]); } 
        }

        public static async Task AcquireToken(AzureEnvs env)
        {
            var tokenCache = new Dictionary<TokenCacheKey, string>();

            _env = new Lazy<AzureEnvs>(() => env);

            var authResult = await GetAuthorizationResult(tokenCache, AADTenantId);
            Trace.WriteLine(String.Format("Welcome {0} (Tenant: {1})", authResult.UserInfo.UserId, authResult.TenantId));

            var tenants = await GetTokenForTenants(tokenCache, authResult);

            SaveRecentEnv(env);
            SaveRecentToken(authResult);
            TokenCache.SaveCache(tokenCache);
        }

        public static void ClearTokenCache()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".csm");
            Directory.CreateDirectory(path);
            foreach (var file in Directory.GetFiles(path))
            {
                Console.Write("Deleting {0} ... ", file);
                File.Delete(file);
                Trace.WriteLine("Done!");
            }
        }

        public static void DumpTokenCache()
        {
            var tokenCache = TokenCache.GetCache();
            var tenantCache = TenantCache.GetCache();
            if (tokenCache.Count > 0)
            {
                foreach (var value in tokenCache.Values.ToArray())
                {
                    var authResult = AuthenticationResult.Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
                    var tenantId = authResult.TenantId;

                    if (InfrastructureTenantIds.Contains(tenantId))
                    {
                        continue;
                    }

                    var user = authResult.UserInfo.UserId;
                    var details = tenantCache[tenantId];
                    Trace.WriteLine(String.Format("User: {0}, Tenant: {1} {2} ({3})", user, tenantId, details.displayName, details.domain));

                    var subscriptions = details.subscriptions;
                    Trace.WriteLine(String.Format("\tThere are {0} subscriptions", subscriptions.Length));

                    foreach (var subscription in subscriptions)
                    {
                        Trace.WriteLine(String.Format("\tSubscription {0} ({1})", subscription.subscriptionId, subscription.displayName));
                    }
                    Trace.WriteLine(String.Empty);
                }
            }
        }

        public static AuthenticationResult GetTokenBySpn(string tenantId, string appId, string appKey, AzureEnvs env)
        {
            var tokenCache = new Dictionary<TokenCacheKey, string>();
            var authority = String.Format("{0}/{1}", AADLoginUrls[(int)env], tenantId);
            var context = new AuthenticationContext(
                authority: authority,
                validateAuthority: true,
                tokenCacheStore: tokenCache);
            var credential = new ClientCredential(appId, appKey);
            var authResult = context.AcquireToken("https://management.core.windows.net/", credential);

            SaveRecentToken(authResult);

            //TokenCache.SaveCache(env, tokenCache);
            return authResult;
        }

        public static async Task<AuthenticationResult> GetTokenByTenant(string tenantId)
        {
            bool found = false;
            var tenantCache = TenantCache.GetCache();
            if (tenantCache.ContainsKey(tenantId))
            {
                found = true;
            }

            if (!found)
            {
                foreach (var tenant in tenantCache)
                {
                    if (tenant.Value.subscriptions.Any(s => s.subscriptionId == tenantId))
                    {
                        tenantId = tenant.Key;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                throw new InvalidOperationException(String.Format("Cannot find tenant {0} in cache!", tenantId));
            }

            var tokenCache = TokenCache.GetCache();
            var authResults = tokenCache.Where(p => p.Key.TenantId == tenantId)
                .Select(p => AuthenticationResult.Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(p.Value)))).ToArray();
            if (authResults.Length <= 0)
            {
                throw new InvalidOperationException(String.Format("Cannot find tenant {0} in cache!", tenantId));
            }

            if (authResults.Length > 1)
            {
                foreach (var authResult in authResults)
                {
                    Trace.WriteLine(authResult.UserInfo.UserId);
                }

                throw new InvalidOperationException("Multiple users found.  Please specify user argument!");
            }
            else
            {
                var authResult = authResults[0];
                if (authResult.ExpiresOn <= DateTime.UtcNow)
                {
                    authResult = await GetAuthorizationResult(tokenCache, authResult.TenantId, authResult.UserInfo.UserId);
                    TokenCache.SaveCache(tokenCache);
                }

                SaveRecentToken(authResult);

                return authResult;
            }
        }

        public static async Task<AuthenticationResult> GetTokenBySubscription(string subscriptionId)
        {
            var tenantCache = TenantCache.GetCache();
            var pairs = tenantCache.Where(p => p.Value.subscriptions.Any(subscription => subscriptionId == subscription.subscriptionId)).ToArray();
            if (pairs.Length == 0)
            {
                Trace.WriteLine(String.Empty);
                Trace.WriteLine(String.Format("Cannot find subscription {0} cache!   Use recent token instead.", subscriptionId));
                Trace.WriteLine(String.Empty);

                return await GetRecentToken();
            }

            return await GetTokenByTenant(pairs[0].Key);
        }

        public static bool IsCacheValid()
        {
            var cache = TokenCache.GetCache();
            return cache != null && cache.Count > 0;
        }

        private static Uri GetCSMUri(AzureEnvs envs)
        {
            if (envs == AzureEnvs.Next)
            {
                return new Uri("https://api-next.resources.windows-int.net");
            }
            else if (envs == AzureEnvs.Current)
            {
                return new Uri("https://api-current.resources.windows-int.net");
            }
            else if (envs == AzureEnvs.Dogfood)
            {
                return new Uri("https://api-dogfood.resources.windows-int.net");
            }
            else
            {
                return new Uri("https://management.azure.com");
            }
        }
        
        private static Task<AuthenticationResult> GetAuthorizationResult(Dictionary<TokenCacheKey, string> tokenCache, string tenantId, string user = null)
        {
            var tcs = new TaskCompletionSource<AuthenticationResult>();
            var thread = new Thread(() =>
            {
                try
                {
                    var authority = String.Format("{0}/{1}", AADLoginUrls[(int)_env.Value], tenantId);
                    var context = new AuthenticationContext(
                        authority: authority,
                        validateAuthority: true,
                        tokenCacheStore: tokenCache);

                    AuthenticationResult result = null;
                    if (!string.IsNullOrEmpty(user))
                    {
                        result = context.AcquireToken(
                            resource: "https://management.core.windows.net/",
                            clientId: AADClientId,
                            redirectUri: new Uri(AADRedirectUri),
                            userId: null);
                    }
                    else
                    {
                        result = context.AcquireToken(
                            resource: "https://management.core.windows.net/",
                            clientId: AADClientId,
                            redirectUri: new Uri(AADRedirectUri),
                            promptBehavior: PromptBehavior.Always);
                    }

                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = "AcquireTokenThread";
            thread.Start();

            return tcs.Task;
        }

        private static async Task<IDictionary<string, AuthenticationResult>> GetTokenForTenants(Dictionary<TokenCacheKey, string> tokenCache, AuthenticationResult authResult)
        {
            var tenantIds = await GetTenantIds(authResult);
            Trace.WriteLine(String.Format("User belongs to {1} tenants", authResult.UserInfo.UserId, tenantIds.Length));

            var tenantCache = TenantCache.GetCache();
            var results = new Dictionary<string, AuthenticationResult>();
            foreach (var tenantId in tenantIds)
            {
                var info = new TenantCacheInfo 
                { 
                    tenantId = tenantId,
                    displayName = "unknown",
                    domain = "unknown"
                };

                AuthenticationResult result = null;
                try
                {
                    result = await GetAuthorizationResult(tokenCache, tenantId: tenantId, user: authResult.UserInfo.UserId);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(String.Format("User: {0}, Tenant: {1} {2}", authResult.UserInfo.UserId, tenantId, ex.Message));
                    Trace.WriteLine(String.Empty);
                    continue;
                }

                results[tenantId] = result;
                try
                {
                    var details = await GetTenantDetail(result, tenantId);
                    info.displayName = details.displayName;
                    info.domain = details.verifiedDomains.First(d => d.@default).name;
                    Trace.WriteLine(String.Format("User: {0}, Tenant: {1} {2} ({3})", result.UserInfo.UserId, tenantId, details.displayName, details.verifiedDomains.First(d => d.@default).name));
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(String.Format("User: {0}, Tenant: {1} {2}", result.UserInfo.UserId, tenantId, ex.Message));
                }

                try
                {
                    var subscriptions = await GetSubscriptions(result);
                    Trace.WriteLine(String.Format("\tThere are {0} subscriptions", subscriptions.Length));

                    info.subscriptions = subscriptions.Select(subscription => new SubscriptionCacheInfo 
                    {
                        subscriptionId = subscription.subscriptionId, 
                        displayName = subscription.displayName 
                    }).ToArray(); 

                    foreach (var subscription in subscriptions)
                    {
                        Trace.WriteLine(String.Format("\tSubscription {0} ({1})", subscription.subscriptionId, subscription.displayName));
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("\t{0}!", ex.Message);
                }
                tenantCache[tenantId] = info;
                Trace.WriteLine(String.Empty);
            }

            TenantCache.SaveCache(tenantCache);

            return results;
        }

        private static async Task<string[]> GetTenantIds(AuthenticationResult authResult)
        {
            var env = _env.Value;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", authResult.CreateAuthorizationHeader());

                var url = string.Format("{0}/tenants?api-version={1}", CSMUrls[(int)env], CSMApiVersion);
                using (var response = await client.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsAsync<ResultOf<TenantInfo>>();
                        return result.value.Select(tenant => tenant.tenantId).ToArray();
                    }

                    throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
                }
            }
        }

        private static async Task<SubscriptionInfo[]> GetSubscriptions(AuthenticationResult authResult)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", authResult.CreateAuthorizationHeader());

                var url = string.Format("{0}/subscriptions?api-version={1}", CSMUrls[(int)_env.Value], CSMApiVersion);
                using (var response = await client.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsAsync<ResultOf<SubscriptionInfo>>();
                        return result.value;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    if (content.StartsWith("{"))
                    {
                        var error = (JObject)JObject.Parse(content)["error"];
                        if (error != null)
                        {
                            throw new InvalidOperationException(String.Format("GetSubscriptions {0}, {1}", response.StatusCode, error.Value<string>("message")));
                        }
                    }

                    throw new InvalidOperationException(String.Format("GetSubscriptions {0}, {1}", response.StatusCode, await response.Content.ReadAsStringAsync()));
                }
            }
        }

        public static async Task<TenantDetails> GetTenantDetail(AuthenticationResult authResult, string tenantId)
        {
            if (InfrastructureTenantIds.Contains(tenantId))
            {
                return new TenantDetails
                {
                    objectId = tenantId,
                    displayName = "Infrastructure",
                    verifiedDomains = new[]
                    {
                        new VerifiedDomain
                        {
                            name = "live.com",
                            @default = true
                        }
                    }
                };
            }

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", authResult.CreateAuthorizationHeader());

                var url = string.Format("{0}/{1}/tenantDetails?api-version={2}", AADGraphUrls[(int)_env.Value], tenantId, AADGraphApiVersion);
                using (var response = await client.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsAsync<ResultOf<TenantDetails>>();
                        return result.value[0];
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    if (content.StartsWith("{"))
                    {
                        var error = (JObject)JObject.Parse(content)["odata.error"];
                        if (error != null)
                        {
                            throw new InvalidOperationException(String.Format("GetTenantDetail {0}, {1}", response.StatusCode, error["message"].Value<string>("value")));
                        }
                    }

                    throw new InvalidOperationException(String.Format("GetTenantDetail {0}, {1}", response.StatusCode, await response.Content.ReadAsStringAsync()));
                }
            }
        }

        public static void SaveRecentEnv(AzureEnvs env)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".csm");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "recent_env.txt"), env.ToString());
        }

        public static async Task<AuthenticationResult> GetRecentToken()
        {
            var recentTokenFile = GetRecentTokenFile();
            var authResult = AuthenticationResult.Deserialize(ProtectedFile.ReadAllText(recentTokenFile));
            if (!String.IsNullOrEmpty(authResult.RefreshToken) && authResult.ExpiresOn <= DateTime.UtcNow)
            {
                var tokenCache = TokenCache.GetCache();
                authResult = await GetAuthorizationResult(tokenCache, authResult.TenantId, authResult.UserInfo.UserId);
                TokenCache.SaveCache(tokenCache);
                SaveRecentToken(authResult);
            }

            return authResult;
        }

        public static void SaveRecentToken(AuthenticationResult authResult)
        {
            ProtectedFile.WriteAllText(GetRecentTokenFile(), authResult.Serialize());
        }

        private static string GetRecentTokenFile()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".csm");
            Directory.CreateDirectory(path);
            return Path.Combine(path, "recent_token.dat");
        }

        public class ResultOf<T>
        {
            public T[] value { get; set; }
        }

        public class TenantInfo
        {
            public string id { get; set; }
            public string tenantId { get; set; }
        }

        public class TenantDetails
        {
            public string objectId { get; set; }
            public string displayName { get; set; }
            public VerifiedDomain[] verifiedDomains { get; set; }
        }

        public class VerifiedDomain
        {
            public bool @default { get; set; }
            public string name { get; set; }
        }

        public class SubscriptionInfo
        {
            public string id { get; set; }
            public string subscriptionId { get; set; }
            public string displayName { get; set; }
            public string state { get; set; }
        }
    }
}
