using Microsoft.Identity.Client;
using System;
using System.IO;
using System.Threading.Tasks;

namespace leituraWPF.Services
{
    public sealed class TokenService
    {
        private readonly leituraWPF.AppConfig _cfg;
        private readonly IConfidentialClientApplication _app;
        private readonly string _cachePath;

        public TokenService(leituraWPF.AppConfig cfg)
        {
            _cfg = cfg;

            _cachePath = Path.Combine(AppContext.BaseDirectory, "msal_cache.bin3");

            _app = ConfidentialClientApplicationBuilder
                .Create(_cfg.ClientId)
                .WithClientSecret(_cfg.ClientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{_cfg.TenantId}")
                .Build();

            // Persistência simples do token (client credentials) entre execuções.
            var cache = _app.AppTokenCache;
            cache.SetBeforeAccess(args =>
            {
                try
                {
                    if (File.Exists(_cachePath))
                    {
                        var data = File.ReadAllBytes(_cachePath);
                        args.TokenCache.DeserializeMsalV3(data, shouldClearExistingCache: true);
                    }
                }
                catch { /* melhor falhar silenciosamente do que travar */ }
            });
            cache.SetAfterAccess(args =>
            {
                try
                {
                    if (args.HasStateChanged)
                    {
                        var data = args.TokenCache.SerializeMsalV3();
                        File.WriteAllBytes(_cachePath, data);
                    }
                }
                catch { /* idem */ }
            });
        }

        public async Task<string> GetTokenAsync()
        {
            var scopes = new[] { _cfg.GraphScope };
            var result = await _app.AcquireTokenForClient(scopes).ExecuteAsync().ConfigureAwait(false);
            return result.AccessToken;
        }
    }
}
