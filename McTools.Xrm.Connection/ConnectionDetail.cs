﻿using McTools.Xrm.Connection.AppCode;
using McTools.Xrm.Connection.Forms;
using McTools.Xrm.Connection.Utils;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;
using Microsoft.Xrm.Sdk.Discovery;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using AuthenticationType = Microsoft.Xrm.Tooling.Connector.AuthenticationType;

namespace McTools.Xrm.Connection
{
    public enum BrowserEnum
    {
        Edge,
        Chrome,
        Firefox,
        None
    }

    public enum SensitiveDataNotFoundReason
    {
        NotAllowedByUser,
        NotAccessible,
        None
    }

    public static class ConnectionDetailExtensions
    {
        public static void OpenUrlWithBrowserProfile(this ConnectionDetail detail, Uri uri)
        {
            var process = new Process();

            if (detail == null)
            {
                Process.Start(uri.ToString());
                return;
            }

            switch (detail.BrowserName)
            {
                case BrowserEnum.Chrome:
                    process.StartInfo = new ProcessStartInfo("chrome.exe");
                    process.StartInfo.Arguments = uri.ToString();
                    process.StartInfo.Arguments += $" --profile-directory=\"{detail.BrowserProfile}\"";
                    break;

                case BrowserEnum.Edge:
                    process.StartInfo = new ProcessStartInfo("msedge.exe");
                    process.StartInfo.Arguments = uri.ToString();
                    process.StartInfo.Arguments += $" --profile-directory=\"{detail.BrowserProfile}\"";
                    break;

                case BrowserEnum.Firefox:
                    process.StartInfo = new ProcessStartInfo("firefox.exe");
                    process.StartInfo.Arguments = uri.ToString();
                    process.StartInfo.Arguments += $" -P \"{detail.BrowserProfile}\"";
                    break;

                default:
                    Process.Start(uri.ToString());
                    return;
            }

            process.Start();
        }
    }

    public class CertificateInfo
    {
        public string Issuer { get; set; }
        public string Name { get; set; }
        public string Thumbprint { get; set; }
    }

    /// <summary>
    /// Stores data regarding a specific connection to Crm server
    /// </summary>
    [XmlInclude(typeof(CertificateInfo))]
    [XmlInclude(typeof(EnvironmentHighlighting))]
    public class ConnectionDetail : IComparable, ICloneable
    {
        private bool? canImpersonate;
        private string clientSecret;
        private Guid impersonatedUserId;
        private string impersonatedUserName;
        private string userPassword;

        #region Constructeur

        static ConnectionDetail()
        {
            // Generate the contracts used by JSON.NET to serialize the metadata cache in the background.
            // This saves about 0.5 seconds on the first connection.
            Task.Run(() =>
            {
                MetadataCacheContractResolver.Instance.PreloadContracts(typeof(MetadataCache));
            });
        }

        public ConnectionDetail()
        {
        }

        public ConnectionDetail(bool createNewId = false)
        {
            if (createNewId)
            {
                ConnectionId = Guid.NewGuid();
            }
        }

        #endregion Constructeur

        #region Propriétés

        private MetadataCache _metadataCache;

        [XmlIgnore]
        private CrmServiceClient crmSvc;

        [XmlIgnore]
        public bool AllowPasswordSharing { get; set; }

        public AuthenticationProviderType AuthType { get; set; }
        public Guid AzureAdAppId { get; set; }
        public BrowserEnum BrowserName { get; set; } = BrowserEnum.None;

        public string BrowserProfile { get; set; }

        [XmlIgnore]
        public bool CanImpersonate { get; private set; }

        [XmlElement("CertificateInfo")]
        public CertificateInfo Certificate { get; set; }

        [XmlElement("ClientSecret")]
        public string ClientSecretEncrypted
        {
            get => clientSecret;
            set => clientSecret = value;
        }

        [XmlIgnore]
        public bool ClientSecretIsEmpty => string.IsNullOrEmpty(clientSecret);

        /// <summary>
        /// Gets or sets the connection unique identifier
        /// </summary>
        public Guid? ConnectionId { get; set; }

        /// <summary>
        /// Gets or sets the name of the connection
        /// </summary>
        public string ConnectionName { get; set; }

        public string ConnectionString { get; set; }

        [XmlIgnore]
        public Color? EnvironmentColor { get; set; }

        ///// <summary>
        ///// Gets or sets custom information for use by consuming application
        ///// </summary>
        //public Dictionary<string, string> CustomInformation { get; set; }
        public EnvironmentHighlighting EnvironmentHighlightingInfo { get; set; }

        public string EnvironmentId { get; set; }

        [XmlIgnore]
        public string EnvironmentText { get; set; }

        [XmlIgnore]
        public Color? EnvironmentTextColor { get; set; }

        /// <summary>
        /// Gets or sets the Home realm url for ADFS authentication
        /// </summary>
        public string HomeRealmUrl { get; set; }

        [XmlIgnore]
        public Guid ImpersonatedUserId => impersonatedUserId;

        [XmlIgnore]
        public string ImpersonatedUserName => impersonatedUserName;

        /// <summary>
        /// Get or set flag to know if custom authentication
        /// </summary>
        public bool IsCustomAuth { get; set; }

        [XmlIgnore] public bool IsEnvironmentHighlightSet => EnvironmentHighlightingInfo != null;
        public bool IsFromSdkLoginCtrl { get; set; }

        [XmlIgnore]
        public DateTime LastUsedOn { get; set; }

        [XmlElement("LastUsedOn")]
        public string LastUsedOnString
        {
            get => LastUsedOn.ToString("yyyy-MM-dd HH:mm:ss");
            set
            {
                //if (DateTime.TryParse(value, CultureInfo.CurrentUICulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
                if (DateTime.TryParseExact(value, "MM/dd/yyyy HH:mm:ss", CultureInfo.CurrentUICulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
                {
                    LastUsedOn = parsed;
                }
                else
                {
                    LastUsedOn = DateTime.Parse(value);
                }
            }
        }

        /// <summary>
        /// Returns a cached version of the metadata for this connection.
        /// </summary>
        /// <remarks>
        /// This cache is updated at the start of each connection, or by calling <see cref="UpdateMetadataCache(bool)"/>
        /// </remarks>
        [XmlIgnore]
        public EntityMetadata[] MetadataCache => _metadataCache?.EntityMetadata;

        /// <summary>
        /// Returns a task that provides access to the <see cref="MetadataCache"/> once it has finished loading
        /// </summary>
        [XmlIgnore]
        public Task<MetadataCache> MetadataCacheLoader { get; private set; } = Task.FromResult<MetadataCache>(null);

        public AuthenticationType NewAuthType { get; set; }

        /// <summary>
        /// Get or set the organization name
        /// </summary>
        public string Organization { get; set; }

        public string OrganizationDataServiceUrl { get; set; }

        /// <summary>
        /// Get or set the organization friendly name
        /// </summary>
        public string OrganizationFriendlyName { get; set; }

        public int OrganizationMajorVersion => OrganizationVersion != null ? int.Parse(OrganizationVersion.Split('.')[0]) : -1;
        public int OrganizationMinorVersion => OrganizationVersion != null ? int.Parse(OrganizationVersion.Split('.')[1]) : -1;

        /// <summary>
        /// Gets or sets the Crm Service Url
        /// </summary>
        public string OrganizationServiceUrl { get; set; }

        /// <summary>
        /// Get or set the organization name
        /// </summary>
        public string OrganizationUrlName { get; set; }

        public string OrganizationVersion { get; set; }
        public string OriginalUrl { get; set; }

        /// <summary>
        /// Gets an information if the password is empty
        /// </summary>
        public bool PasswordIsEmpty => string.IsNullOrEmpty(userPassword);

        /// <summary>
        /// OAuth Refresh Token
        /// </summary>
        public string RefreshToken { get; set; }

        public string ReplyUrl { get; set; }

        /// <summary>
        /// Client Secret used for S2S Auth
        /// </summary>
        public string S2SClientSecret
        {
            get => clientSecret;
            set => clientSecret = value;
        }

        /// <summary>
        /// Gets or sets the information if the password must be saved
        /// </summary>
        public bool SavePassword { get; set; }

        /// <summary>
        /// Get or set the server name
        /// </summary>
        public string ServerName { get; set; }

        /// <summary>
        /// Get or set the server port
        /// </summary>
        [DefaultValue(80)]
        [XmlIgnore]
        public int? ServerPort { get; set; }

        [XmlElement("ServerPort")]
        public string ServerPortString
        {
            get => ServerPort.ToString();
            set => ServerPort = string.IsNullOrEmpty(value) ? 80 : int.Parse(value);
        }

        [XmlIgnore]
        public CrmServiceClient ServiceClient
        {
            get => GetCrmServiceClient();
            set
            {
                crmSvc = value;
                SetImpersonationCapability();
            }
        }

        public Guid TenantId { get; set; }
        public TimeSpan Timeout { get; set; }

        public long TimeoutTicks
        {
            get { return Timeout.Ticks; }
            set { Timeout = new TimeSpan(value); }
        }

        [XmlIgnore]
        public bool UseConnectionString => !string.IsNullOrEmpty(ConnectionString);

        /// <summary>
        /// Get or set flag to know if we use IFD
        /// </summary>
        public bool UseIfd { get; set; }

        /// <summary>
        /// Get or set flag to know if we use Multi Factor Authentication
        /// </summary>
        public bool UseMfa { get; set; }

        /// <summary>
        /// Get or set flag to know if we use CRM Online
        /// </summary>
        [XmlIgnore]
        public bool UseOnline => OriginalUrl.IndexOf(".dynamics.com", StringComparison.InvariantCultureIgnoreCase) > 0;

        /// <summary>
        /// Get or set the user domain name
        /// </summary>
        public string UserDomain { get; set; }

        /// <summary>
        /// Get or set flag to know if we use Online Services
        /// </summary>
        //public bool UseOsdp { get; set; }
        /// <summary>
        /// Get or set user login
        /// </summary>
        public string UserName { get; set; }

        [XmlElement("UserPassword")]
        public string UserPasswordEncrypted
        {
            get => userPassword;
            set => userPassword = value;
        }

        /// <summary>
        /// Get or set the use of SSL connection
        /// </summary>
        [XmlIgnore]
        public bool UseSsl => WebApplicationUrl?.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase) ?? OriginalUrl.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase);

        public string WebApplicationUrl
        {
            get;
            set;
        }

        #endregion Propriétés

        #region Méthodes

        public void ErasePassword()
        {
            userPassword = null;
            clientSecret = null;
        }

        public CrmServiceClient GetCrmServiceClient(bool forceNewService = false)
        {
            if (forceNewService == false && crmSvc != null)
            {
                SetImpersonationCapability();

                return crmSvc;
            }
            if (Timeout.Ticks == 0)
            {
                Timeout = new TimeSpan(0, 2, 0);
            }
            CrmServiceClient.MaxConnectionTimeout = Timeout;

            if (IsFromSdkLoginCtrl)
            {
                if (crmSvc != null)
                {
                    SetImpersonationCapability();

                    return crmSvc;
                }

                throw new ApplicationException("Connections using the SDK Login Control cannot be created automatically");
            }
            else if (Certificate != null)
            {
                var cs = HandleConnectionString($"AuthType=Certificate;url={OriginalUrl};thumbprint={Certificate.Thumbprint};ClientId={AzureAdAppId};");
                crmSvc = new CrmServiceClient(cs);
            }
            else if (!string.IsNullOrEmpty(ConnectionString))
            {
                var cs = HandleConnectionString(ConnectionString);
                crmSvc = new CrmServiceClient(cs);
            }
            else if (NewAuthType == AuthenticationType.ClientSecret)
            {
                var cs = HandleConnectionString($"AuthType=ClientSecret;url={OriginalUrl};ClientId={AzureAdAppId};ClientSecret={clientSecret}");
                crmSvc = new CrmServiceClient(cs);
            }
            else if (NewAuthType == AuthenticationType.OAuth && UseMfa)
            {
                CrmServiceClient.AuthOverrideHook = new MfaAuthOverride(this);
                crmSvc = new CrmServiceClient(new Uri(OriginalUrl), true);
                CrmServiceClient.AuthOverrideHook = null;
            }
            else if (!string.IsNullOrEmpty(clientSecret))
            {
                ConnectOAuth();
            }
            else if (UseOnline)
            {
                ConnectOnline();

                //var path = Path.Combine(Path.GetTempPath(), ConnectionId.Value.ToString("B"));

                //var cs = HandleConnectionString($"AuthType=OAuth;Username={UserName};Password={userPassword};Url={OriginalUrl};AppId={(AzureAdAppId != Guid.Empty ? AzureAdAppId : new Guid("51f81489-12ee-4a9e-aaae-a2591f45987d"))};RedirectUri={(string.IsNullOrEmpty(ReplyUrl) ? "app://58145B91-0C36-4500-8554-080854F2AC97" : ReplyUrl)};TokenCacheStorePath={path};LoginPrompt=Auto");
                //crmSvc = new CrmServiceClient(cs);
            }
            else if (UseIfd)
            {
                ConnectIfd();
            }
            else
            {
                ConnectOnprem();
            }

            if (!crmSvc.IsReady)
            {
                var error = crmSvc.LastCrmError;
                crmSvc = null;
                throw new Exception(error);
            }

            SetImpersonationCapability();

            OrganizationFriendlyName = crmSvc.ConnectedOrgFriendlyName;
            OrganizationDataServiceUrl = crmSvc.ConnectedOrgPublishedEndpoints[EndpointType.OrganizationDataService];
            OrganizationServiceUrl = crmSvc.ConnectedOrgPublishedEndpoints[EndpointType.OrganizationService];
            WebApplicationUrl = crmSvc.ConnectedOrgPublishedEndpoints[EndpointType.WebApplication];
            Organization = crmSvc.ConnectedOrgUniqueName;
            OrganizationVersion = crmSvc.ConnectedOrgVersion.ToString();
            TenantId = crmSvc.TenantId;
            EnvironmentId = crmSvc.EnvironmentId;

            var webAppURi = new Uri(WebApplicationUrl);
            ServerName = webAppURi.Host;
            ServerPort = webAppURi.Port;

            //UseIfd = crmSvc.ActiveAuthenticationType == AuthenticationType.IFD;

            switch (crmSvc.ActiveAuthenticationType)
            {
                case AuthenticationType.AD:
                case AuthenticationType.Claims:
                    AuthType = AuthenticationProviderType.ActiveDirectory;
                    break;

                case AuthenticationType.IFD:
                    AuthType = AuthenticationProviderType.Federation;
                    break;

                case AuthenticationType.Live:
                    AuthType = AuthenticationProviderType.LiveId;
                    break;

                case AuthenticationType.OAuth:
                    // TODO add new property in ConnectionDetail class?
                    break;

                case AuthenticationType.Office365:
                    AuthType = AuthenticationProviderType.OnlineFederation;
                    break;
            }

            return crmSvc;
        }

        public void SetClientSecret(string secret, bool isEncrypted = false)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                if (isEncrypted)
                {
                    clientSecret = secret;
                }
                else
                {
                    clientSecret = CryptoManager.Encrypt(secret, ConnectionManager.CryptoPassPhrase,
                        ConnectionManager.CryptoSaltValue,
                        ConnectionManager.CryptoHashAlgorythm,
                        ConnectionManager.CryptoPasswordIterations,
                        ConnectionManager.CryptoInitVector,
                        ConnectionManager.CryptoKeySize);
                }
            }
        }

        public void SetConnectionString(string connectionString)
        {
            var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };

            OriginalUrl = (csb.ContainsKey("ServiceUri") ? csb["ServiceUri"] :
                csb.ContainsKey("Service Uri") ? csb["Service Uri"] :
                csb.ContainsKey("Url") ? csb["Url"] :
                csb.ContainsKey("Server") ? csb["Server"] : "").ToString();

            if (csb.ContainsKey("Password"))
            {
                csb["Password"] = CryptoManager.Encrypt(csb["Password"].ToString(), ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);
            }
            if (csb.ContainsKey("ClientSecret"))
            {
                csb["ClientSecret"] = CryptoManager.Encrypt(csb["ClientSecret"].ToString(), ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);
            }

            ConnectionString = csb.ToString();
        }

        public void SetPassword(string password, bool isEncrypted = false)
        {
            if (!string.IsNullOrEmpty(password))
            {
                string newPassword;
                if (isEncrypted)
                {
                    newPassword = password;
                }
                else
                {
                    newPassword = CryptoManager.Encrypt(password, ConnectionManager.CryptoPassPhrase,
                        ConnectionManager.CryptoSaltValue,
                        ConnectionManager.CryptoHashAlgorythm,
                        ConnectionManager.CryptoPasswordIterations,
                        ConnectionManager.CryptoInitVector,
                        ConnectionManager.CryptoKeySize);
                }

                if (NewAuthType == AuthenticationType.ClientSecret)
                {
                    clientSecret = newPassword;
                }
                else
                {
                    userPassword = newPassword;
                }
            }
        }

        /// <summary>
        /// Retourne le nom de la connexion
        /// </summary>
        /// <returns>Nom de la connexion</returns>
        public override string ToString()
        {
            return ConnectionName;
        }

        public bool TryRequestClientSecret(Control parent, string secretUsageDescription, out string secret, out SensitiveDataNotFoundReason notFoundReason)
        {
            var prd = new PasswordRequestDialog(secretUsageDescription, this, "client secret");
            if (AllowPasswordSharing || prd.ShowDialog(parent) == DialogResult.OK && prd.Accepted)
            {
                if (string.IsNullOrEmpty(clientSecret))
                {
                    secret = string.Empty;
                    notFoundReason = SensitiveDataNotFoundReason.NotAccessible;
                    return false;
                }

                secret = CryptoManager.Decrypt(clientSecret, ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);

                notFoundReason = SensitiveDataNotFoundReason.None;
                return true;
            }

            notFoundReason = SensitiveDataNotFoundReason.NotAllowedByUser;
            secret = string.Empty;
            return false;
        }

        public bool TryRequestPassword(Control parent, string passwordUsageDescription, out string password, out SensitiveDataNotFoundReason notFoundReason)
        {
            var prd = new PasswordRequestDialog(passwordUsageDescription, this, "password");
            if (AllowPasswordSharing || prd.ShowDialog(parent) == DialogResult.OK && prd.Accepted)
            {
                if (string.IsNullOrEmpty(userPassword))
                {
                    password = string.Empty;
                    notFoundReason = SensitiveDataNotFoundReason.NotAccessible;
                    return false;
                }

                password = CryptoManager.Decrypt(userPassword, ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);

                notFoundReason = SensitiveDataNotFoundReason.None;
                return true;
            }

            notFoundReason = SensitiveDataNotFoundReason.NotAllowedByUser;
            password = string.Empty;
            return false;
        }

        public void UpdateAfterEdit(ConnectionDetail editedConnection)
        {
            ConnectionName = editedConnection.ConnectionName;
            ConnectionString = editedConnection.ConnectionString;
            OrganizationServiceUrl = editedConnection.OrganizationServiceUrl;
            OrganizationDataServiceUrl = editedConnection.OrganizationDataServiceUrl;
            Organization = editedConnection.Organization;
            OrganizationFriendlyName = editedConnection.OrganizationFriendlyName;
            ServerName = editedConnection.ServerName;
            ServerPort = editedConnection.ServerPort;
            UseIfd = editedConnection.UseIfd;
            UserDomain = editedConnection.UserDomain;
            UserName = editedConnection.UserName;
            userPassword = editedConnection.userPassword;
            HomeRealmUrl = editedConnection.HomeRealmUrl;
            Timeout = editedConnection.Timeout;
            UseMfa = editedConnection.UseMfa;
            ReplyUrl = editedConnection.ReplyUrl;
            AzureAdAppId = editedConnection.AzureAdAppId;
            clientSecret = editedConnection.clientSecret;
            RefreshToken = editedConnection.RefreshToken;
            EnvironmentText = editedConnection.EnvironmentText;
            EnvironmentColor = editedConnection.EnvironmentColor;
            EnvironmentTextColor = editedConnection.EnvironmentTextColor;

            TenantId = editedConnection.TenantId;
            EnvironmentId = editedConnection.EnvironmentId;
            AllowPasswordSharing = editedConnection.AllowPasswordSharing;
            BrowserName = editedConnection.BrowserName;
            BrowserProfile = editedConnection.BrowserProfile;
            IsCustomAuth = editedConnection.IsCustomAuth;
            NewAuthType = editedConnection.NewAuthType;
        }

        private void ConnectIfd()
        {
            AuthType = AuthenticationProviderType.Federation;

            if (!IsCustomAuth)
            {
                crmSvc = new CrmServiceClient(CredentialCache.DefaultNetworkCredentials,
                    AuthenticationType.IFD,
                    ServerName,
                    ServerPort.ToString(),
                    OrganizationUrlName,
                    true,
                    UseSsl);
            }
            else
            {
                var password = CryptoManager.Decrypt(userPassword, ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);

                crmSvc = new CrmServiceClient(UserName, CrmServiceClient.MakeSecureString(password), UserDomain,
                    HomeRealmUrl,
                    ServerName,
                    ServerPort.ToString(),
                    OrganizationUrlName,
                    true,
                    UseSsl);
            }
        }

        private void ConnectOAuth()
        {
            if (!string.IsNullOrEmpty(RefreshToken))
            {
                CrmServiceClient.AuthOverrideHook = new RefreshTokenAuthOverride(this);
                crmSvc = new CrmServiceClient(new Uri($"https://{ServerName}:{ServerPort}"), true);
                CrmServiceClient.AuthOverrideHook = null;
            }
            else
            {
                var secret = CryptoManager.Decrypt(clientSecret, ConnectionManager.CryptoPassPhrase,
                     ConnectionManager.CryptoSaltValue,
                     ConnectionManager.CryptoHashAlgorythm,
                     ConnectionManager.CryptoPasswordIterations,
                     ConnectionManager.CryptoInitVector,
                     ConnectionManager.CryptoKeySize);

                var path = Path.Combine(Path.GetTempPath(), ConnectionId.Value.ToString("B"), "oauth-cache.txt");
                crmSvc = new CrmServiceClient(new Uri($"https://{ServerName}:{ServerPort}"), AzureAdAppId.ToString(), CrmServiceClient.MakeSecureString(secret), true, path);
            }
        }

        private void ConnectOnline()
        {
            AuthType = AuthenticationProviderType.OnlineFederation;

            if (string.IsNullOrEmpty(userPassword))
            {
                throw new Exception("Unable to read user password");
            }

            var password = CryptoManager.Decrypt(userPassword, ConnectionManager.CryptoPassPhrase,
                 ConnectionManager.CryptoSaltValue,
                 ConnectionManager.CryptoHashAlgorythm,
                 ConnectionManager.CryptoPasswordIterations,
                 ConnectionManager.CryptoInitVector,
                 ConnectionManager.CryptoKeySize);

            Utilities.GetOrgnameAndOnlineRegionFromServiceUri(new Uri(OriginalUrl), out var region, out var orgName, out _);

            var path = Path.Combine(Path.GetTempPath(), ConnectionId.Value.ToString("B"), "oauth-cache.txt");

            crmSvc = new CrmServiceClient(UserName, CrmServiceClient.MakeSecureString(password),
                region,
                orgName,
                true,
                null,
                null,
                AzureAdAppId != Guid.Empty ? AzureAdAppId.ToString() : "51f81489-12ee-4a9e-aaae-a2591f45987d",
                new Uri(string.IsNullOrEmpty(ReplyUrl) ? "app://58145B91-0C36-4500-8554-080854F2AC97" : ReplyUrl),
                path,
                null);
        }

        private void ConnectOnprem()
        {
            AuthType = AuthenticationProviderType.ActiveDirectory;

            NetworkCredential credential;
            if (!IsCustomAuth)
            {
                credential = CredentialCache.DefaultNetworkCredentials;
            }
            else
            {
                var password = CryptoManager.Decrypt(userPassword, ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);

                credential = new NetworkCredential(UserName, password, UserDomain);
            }

            crmSvc = new CrmServiceClient(credential,
                AuthenticationType.AD,
                ServerName,
                ServerPort.ToString(),
                OrganizationUrlName,
                true,
                UseSsl);
        }

        private string HandleConnectionString(string connectionString)
        {
            var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (csb.ContainsKey("timeout"))
            {
                var csTimeout = TimeSpan.Parse(csb["timeout"].ToString());
                csb.Remove("timeout");
                CrmServiceClient.MaxConnectionTimeout = csTimeout;
            }

            OriginalUrl = csb["Url"].ToString();
            UserName = csb.ContainsKey("username") ? csb["username"].ToString() :
                csb.ContainsKey("clientid") ? csb["clientid"].ToString() : null;

            if (csb.ContainsKey("Password"))
            {
                csb["Password"] = CryptoManager.Decrypt(csb["Password"].ToString(), ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);
            }
            if (csb.ContainsKey("ClientSecret"))
            {
                csb["ClientSecret"] = CryptoManager.Decrypt(csb["ClientSecret"].ToString(), ConnectionManager.CryptoPassPhrase,
                    ConnectionManager.CryptoSaltValue,
                    ConnectionManager.CryptoHashAlgorythm,
                    ConnectionManager.CryptoPasswordIterations,
                    ConnectionManager.CryptoInitVector,
                    ConnectionManager.CryptoKeySize);
            }

            var cs = csb.ToString();

            if (cs.IndexOf("RequireNewInstance=", StringComparison.Ordinal) < 0)
            {
                if (!cs.EndsWith(";"))
                {
                    cs += ";";
                }

                cs += "RequireNewInstance=True;";
            }

            return cs;
        }

        private void SetImpersonationCapability()
        {
            if (canImpersonate == null)
            {
                var query = new QueryExpression("systemuserroles")
                {
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("systemuserid", ConditionOperator.EqualUserId)
                        }
                    },
                    LinkEntities =
                    {
                        new LinkEntity
                        {
                            LinkFromEntityName = "systemuserroles",
                            LinkFromAttributeName = "roleid",
                            LinkToAttributeName = "roleid",
                            LinkToEntityName = "role",

                            LinkEntities =
                            {
                                new LinkEntity
                                {
                                    LinkFromEntityName = "role",
                                    LinkFromAttributeName = "roleid",
                                    LinkToAttributeName = "roleid",
                                    LinkToEntityName = "roleprivileges",
                                    EntityAlias = "priv",
                                    Columns = new ColumnSet("privilegedepthmask"),
                                    LinkEntities =
                                    {
                                        new LinkEntity
                                        {
                                            LinkFromEntityName = "roleprivileges",
                                            LinkFromAttributeName = "privilegeid",
                                            LinkToAttributeName = "privilegeid",
                                            LinkToEntityName = "privilege", LinkCriteria = new FilterExpression
                                            {
                                                Conditions =
                                                {
                                                    new ConditionExpression("name", ConditionOperator.Equal, "prvActOnBehalfOfAnotherUser"),
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                };

                try
                {
                    var privileges = crmSvc.RetrieveMultiple(query).Entities;

                    canImpersonate = privileges.Any(p =>
                        (int)p.GetAttributeValue<AliasedValue>("priv.privilegedepthmask").Value == 8);
                }
                catch
                {
                    canImpersonate = false;
                }
            }

            CanImpersonate = canImpersonate.Value;
        }

        #endregion Méthodes

        public object Clone()
        {
            var cd = new ConnectionDetail
            {
                AuthType = AuthType,
                ConnectionId = Guid.NewGuid(),
                ConnectionName = ConnectionName,
                ConnectionString = ConnectionString,
                HomeRealmUrl = HomeRealmUrl,
                Organization = Organization,
                OrganizationFriendlyName = OrganizationFriendlyName,
                OrganizationServiceUrl = OrganizationServiceUrl,
                OrganizationDataServiceUrl = OrganizationDataServiceUrl,
                OrganizationUrlName = OrganizationUrlName,
                OrganizationVersion = OrganizationVersion,
                SavePassword = SavePassword,
                ServerName = ServerName,
                ServerPort = ServerPort,
                TimeoutTicks = TimeoutTicks,
                UseIfd = UseIfd,
                UserDomain = UserDomain,
                UserName = UserName,
                userPassword = userPassword,
                WebApplicationUrl = WebApplicationUrl,
                OriginalUrl = OriginalUrl,
                Timeout = Timeout,
                UseMfa = UseMfa,
                AzureAdAppId = AzureAdAppId,
                ReplyUrl = ReplyUrl,
                EnvironmentText = EnvironmentText,
                EnvironmentColor = EnvironmentColor,
                EnvironmentTextColor = EnvironmentTextColor,
                RefreshToken = RefreshToken,
                S2SClientSecret = S2SClientSecret,
                IsFromSdkLoginCtrl = IsFromSdkLoginCtrl,
                TenantId = TenantId,
                EnvironmentId = EnvironmentId,
                AllowPasswordSharing = AllowPasswordSharing,
                BrowserName = BrowserName,
                BrowserProfile = BrowserProfile,
                IsCustomAuth = IsCustomAuth,
                NewAuthType = NewAuthType,
                Certificate = Certificate,
                ClientSecretEncrypted = ClientSecretEncrypted,
                UserPasswordEncrypted = UserPasswordEncrypted,
                clientSecret = clientSecret
            };

            if (Certificate != null)
            {
                cd.Certificate = new CertificateInfo
                {
                    Issuer = Certificate.Issuer,
                    Thumbprint = Certificate.Thumbprint,
                    Name = Certificate.Name
                };
            }

            return cd;
        }

        public void CopyClientSecretTo(ConnectionDetail detail)
        {
            detail.clientSecret = clientSecret;
        }

        public void CopyPasswordTo(ConnectionDetail detail)
        {
            detail.userPassword = userPassword;
        }

        public string GetConnectionString()
        {
            var csb = new DbConnectionStringBuilder();

            switch (AuthType)
            {
                default:
                    csb["AuthType"] = "AD";
                    break;

                case AuthenticationProviderType.OnlineFederation:
                    csb["AuthType"] = "Office365";
                    break;

                case AuthenticationProviderType.Federation:
                    csb["AuthType"] = "IFD";
                    break;
            }

            csb["Url"] = WebApplicationUrl;

            if (Certificate != null)
            {
                csb["AuthType"] = "Certificate";
                csb["ClientId"] = AzureAdAppId;
                csb["Thumbprint"] = Certificate.Thumbprint;

                return csb.ToString();
            }

            if (!string.IsNullOrEmpty(clientSecret))
            {
                csb["AuthType"] = "ClientSecret";
                csb["ClientId"] = AzureAdAppId.ToString("B");
                csb["ClientSecret"] = "*************";

                return csb.ToString();
            }

            if (UseMfa)
            {
                csb["Username"] = UserName;
                csb["AuthType"] = "OAuth";
                csb["ClientId"] = AzureAdAppId.ToString("B");
                csb["LoginPrompt"] = "Auto";
                csb["RedirectUri"] = ReplyUrl;
                csb["TokenCacheStorePath"] = Path.Combine(Path.GetTempPath(), ConnectionId.Value.ToString("B"), "oauth-cache.txt");

                return csb.ToString();
            }

            if (!string.IsNullOrEmpty(UserDomain))
                csb["Domain"] = UserDomain;
            csb["Username"] = UserName;
            csb["Password"] = "********";

            if (!string.IsNullOrEmpty(HomeRealmUrl))
                csb["HomeRealmUri"] = HomeRealmUrl;

            return csb.ToString();
        }

        public bool IsConnectionBrokenWithUpdatedData(ConnectionDetail originalDetail)
        {
            if (originalDetail == null)
            {
                return true;
            }

            if (originalDetail.HomeRealmUrl != HomeRealmUrl
                || originalDetail.IsCustomAuth != IsCustomAuth
                || originalDetail.Organization != Organization
                || originalDetail.OrganizationUrlName != OrganizationUrlName
                || originalDetail.ServerName.ToLower() != ServerName.ToLower()
                || originalDetail.ServerPort != ServerPort
                || originalDetail.UseIfd != UseIfd
                || originalDetail.UseOnline != UseOnline
                || originalDetail.UseSsl != UseSsl
                || originalDetail.UseMfa != UseMfa
                || originalDetail.clientSecret != clientSecret
                || originalDetail.AzureAdAppId != AzureAdAppId
                || originalDetail.ReplyUrl != ReplyUrl
                || originalDetail.UserDomain?.ToLower() != UserDomain?.ToLower()
                || originalDetail.UserName?.ToLower() != UserName?.ToLower()
                || SavePassword && !string.IsNullOrEmpty(userPassword) && originalDetail.userPassword != userPassword
                || SavePassword && !string.IsNullOrEmpty(clientSecret) && originalDetail.clientSecret != clientSecret
                || originalDetail.Certificate?.Thumbprint != Certificate?.Thumbprint)
            {
                return true;
            }

            return false;
        }

        public bool PasswordIsDifferent(string password)
        {
            return password != userPassword;
        }

        #region IComparable Members

        public int CompareTo(object obj)
        {
            var detail = (ConnectionDetail)obj;

            return String.Compare(ConnectionName, detail.ConnectionName, StringComparison.Ordinal);
        }

        #endregion IComparable Members

        #region Impersonation methods

        public event EventHandler<ImpersonationEventArgs> OnImpersonate;

        public void Impersonate(Guid userId, string username = null)
        {
            impersonatedUserId = userId;
            impersonatedUserName = username;

            ServiceClient.CallerId = userId;

            OnImpersonate?.Invoke(this, new ImpersonationEventArgs(impersonatedUserId, impersonatedUserName));
        }

        public void RemoveImpersonation()
        {
            impersonatedUserId = Guid.Empty;
            impersonatedUserName = null;

            ServiceClient.CallerId = Guid.Empty;

            OnImpersonate?.Invoke(this, new ImpersonationEventArgs(impersonatedUserId, impersonatedUserName));
        }

        #endregion Impersonation methods

        #region Metadata Cache methods

        /// <summary>
        /// Updates the <see cref="MetadataCache"/>
        /// </summary>
        /// <param name="flush">Indicates if the existing cache should be flushed and a full new copy of the metadata should be retrieved</param>
        public Task UpdateMetadataCache(bool flush)
        {
            if (OrganizationMajorVersion < 8)
                throw new NotSupportedException("Metadata cache is only supported on Dynamics CRM 2016 or later");

            // If there's already an update in progress, don't start a new one
            if (!MetadataCacheLoader.IsCompleted)
                return MetadataCacheLoader;

            // Load the metadata in a background task
            var task = new Task<MetadataCache>(() =>
            {
                // Load & update metadata cache
                var metadataCachePath = Path.Combine(Path.GetDirectoryName(ConnectionsList.ConnectionsListFilePath), "..", "Metadata", ConnectionId + ".json.gz");
                metadataCachePath = Path.IsPathRooted(metadataCachePath) ? metadataCachePath : Path.Combine(new FileInfo(Assembly.GetExecutingAssembly().Location).DirectoryName, metadataCachePath);

                // Set up the serializer to use. We need to add a custom converter to handle the KeyAttributeCollection on
                // the Entity class (used for the AsyncJob property on EntityKeyMetadata) as the standard JsonSerializer
                // can't handle it. We also need to set the TypeNameHandling property to Auto to ensure polymorphic classes
                // (e.g. attribute types) are serialized correctly.
                var metadataSerializer = new JsonSerializer();
                metadataSerializer.ContractResolver = MetadataCacheContractResolver.Instance;
                metadataSerializer.TypeNameHandling = TypeNameHandling.Auto;
                var metadataCache = _metadataCache;

                // Load the existing file if it exists and we're not trying to just update an already-loaded cache.
                if (metadataCache == null && File.Exists(metadataCachePath) && !flush)
                {
                    try
                    {
                        using (var stream = File.OpenRead(metadataCachePath))
                        using (var gz = new GZipStream(stream, CompressionMode.Decompress))
                        using (var reader = new StreamReader(gz))
                        using (var jsonReader = new JsonTextReader(reader))
                        {
                            metadataCache = metadataSerializer.Deserialize<MetadataCache>(jsonReader);
                        }
                    }
                    catch
                    {
                        // If the cache file isn't readable for any reason, throw it away and download a new copy
                    }
                }

                // Check if the metadata has changed since the last connection
                // If this query changes, increment the version number to ensure any previously cached versions are flushed
                const int queryVersion = 2;

                if (metadataCache != null && metadataCache.MetadataQueryVersion != queryVersion)
                {
                    metadataCache = null;
                    flush = true;
                }

                var metadataQuery = new RetrieveMetadataChangesRequest
                {
                    ClientVersionStamp = !flush ? metadataCache?.ClientVersionStamp : null,
                    Query = new EntityQueryExpression
                    {
                        Properties = new MetadataPropertiesExpression { AllProperties = true },
                        AttributeQuery = new AttributeQueryExpression
                        {
                            Properties = new MetadataPropertiesExpression { AllProperties = true }
                        },
                        RelationshipQuery = new RelationshipQueryExpression
                        {
                            Properties = new MetadataPropertiesExpression { AllProperties = true }
                        }
                    }
                };

                RetrieveMetadataChangesResponse metadataUpdate;
                var svc = ServiceClient;

                // Use a cloned connection instance where possible so we can load the metadata in the background without
                // blocking other uses of the connection.
                if (svc.ActiveAuthenticationType == AuthenticationType.OAuth)
                    svc = svc.Clone();

                try
                {
                    metadataUpdate = (RetrieveMetadataChangesResponse)svc.Execute(metadataQuery);
                }
                catch (FaultException<OrganizationServiceFault> ex)
                {
                    // If the last connection was too long ago, we need to request all the metadata, not just the changes
                    if (ex.Detail.ErrorCode == unchecked((int)0x80044352))
                    {
                        metadataQuery.ClientVersionStamp = null;
                        metadataUpdate = (RetrieveMetadataChangesResponse)svc.Execute(metadataQuery);
                    }
                    else
                    {
                        throw;
                    }
                }

                if (metadataQuery.ClientVersionStamp != null && metadataQuery.ClientVersionStamp != metadataUpdate.ServerVersionStamp)
                {
                    // Something has changed in the metadata. We can't reliably apply changes as some of the IDs
                    // appear to change during backup/restore operations, so get a whole fresh copy
                    metadataQuery.ClientVersionStamp = null;
                    metadataUpdate = (RetrieveMetadataChangesResponse)svc.Execute(metadataQuery);
                }

                if (metadataQuery.ClientVersionStamp == null)
                {
                    // Save the latest metadata cache
                    metadataCache = new MetadataCache();
                    metadataCache.EntityMetadata = metadataUpdate.EntityMetadata.ToArray();
                    metadataCache.ClientVersionStamp = metadataUpdate.ServerVersionStamp;
                    metadataCache.MetadataQueryVersion = queryVersion;

                    _metadataCache = metadataCache;

                    Task.Run(() =>
                    {
                        // Write the new metadata to a temporary file in the same directory, then swap it with the original
                        // file. This avoids getting a corrupted file if something goes wrong while the file is being written.
                        var directory = Path.GetDirectoryName(metadataCachePath);
                        Directory.CreateDirectory(directory);

                        var tempFileName = "~" + Path.GetFileName(metadataCachePath);
                        var tempFilePath = Path.Combine(directory, tempFileName);

                        using (var stream = File.Create(tempFilePath))
                        using (var gz = new GZipStream(stream, CompressionLevel.Optimal))
                        using (var writer = new StreamWriter(gz))
                        using (var jsonWriter = new JsonTextWriter(writer))
                        {
                            metadataSerializer.Serialize(jsonWriter, metadataCache);
                        }

                        if (File.Exists(metadataCachePath))
                            File.Replace(tempFilePath, metadataCachePath, null);
                        else
                            File.Move(tempFilePath, metadataCachePath);
                    });
                }

                return metadataCache;
            });
            task.ConfigureAwait(false);

            // Store the current metadata loading task and run it
            MetadataCacheLoader = task;
            task.Start();
            return task;
        }

        #endregion Metadata Cache methods
    }

    public class EnvironmentHighlighting
    {
        [XmlIgnore]
        public Color? Color { get; set; }

        [XmlElement("Color")]
        public string ColorString
        {
            get => ColorTranslator.ToHtml(Color ?? System.Drawing.Color.Black);
            set => Color = ColorTranslator.FromHtml(value);
        }

        public string Text { get; set; }

        [XmlIgnore]
        public Color? TextColor { get; set; }

        [XmlElement("TextColor")]
        public string TextColorString
        {
            get => ColorTranslator.ToHtml(TextColor ?? System.Drawing.Color.Black);
            set => TextColor = ColorTranslator.FromHtml(value);
        }
    }
}