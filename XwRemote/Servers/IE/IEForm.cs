using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using XwRemote.Settings;

namespace XwRemote.Servers
{
    // Preserve the existing class and ServerType for saved connections.
    public partial class IEForm : Form
    {
        private readonly Server server;
        private Uri loginUri;
        private bool tryAutoLogin = true;
        private bool triedHttpLogin;
        private bool closing;

        public IEForm(Server srv)
        {
            InitializeComponent();
            Dock = DockStyle.Fill;
            TopLevel = false;
            server = srv;
        }

        internal static Uri GetAddress(string host)
        {
            string address = (host ?? "").Trim();
            if (address.Length == 0) throw new ArgumentException("Enter a website address.");
            if (!address.Contains("://")) address = "http://" + address;
            Uri uri;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Enter an HTTP or HTTPS address without credentials in the URL.");
            return uri;
        }

        internal static bool SameOrigin(Uri expected, string address)
        {
            Uri actual;
            return expected != null && Uri.TryCreate(address, UriKind.Absolute, out actual) &&
                expected.Scheme == actual.Scheme && expected.IdnHost == actual.IdnHost && expected.Port == actual.Port;
        }

        private async void OnShown(object sender, EventArgs e)
        {
            try
            {
                loginUri = GetAddress(server.Host);
                string profileKey;
                using (var sha = SHA256.Create())
                    profileKey = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                        server.ID + "\n" + loginUri.GetLeftPart(UriPartial.Authority) + "\n" + server.Username))).Replace("-", "");
                string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "XwRemote", "WebView2", profileKey);
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
                if (closing || IsDisposed) return;
                await webBrowser.EnsureCoreWebView2Async(environment);
                if (closing || IsDisposed) return;
                webBrowser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
                webBrowser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
                webBrowser.CoreWebView2.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
                webBrowser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                webBrowser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                webBrowser.CoreWebView2.Navigate(loginUri.AbsoluteUri);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowBrowserError("Microsoft Edge WebView2 Runtime is required. Install the Evergreen Runtime, then reopen this tab.");
                if (closing || IsDisposed) return;
                var download = new LinkLabel { Text = "Download Microsoft Edge WebView2 Runtime",
                    Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(20, 0, 0, 0) };
                download.LinkClicked += (s, args) =>
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                            "https://developer.microsoft.com/en-us/microsoft-edge/webview2/") { UseShellExecute = true });
                    }
                    catch (Exception)
                    {
                        MessageBox.Show("Download the Evergreen Runtime from https://developer.microsoft.com/microsoft-edge/webview2/", "Web Browser");
                    }
                };
                Controls.Add(download);
                download.BringToFront();
            }
            catch (Exception ex)
            {
                ShowBrowserError("Unable to open the browser: " + ex.Message);
            }
        }

        private void OnBasicAuthenticationRequested(object sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
        {
            if (closing || server.UseHtmlLogin || triedHttpLogin || string.IsNullOrWhiteSpace(server.Username) ||
                !SameOrigin(loginUri, e.Uri)) return;
            triedHttpLogin = true;
            e.Response.UserName = server.Username;
            e.Response.Password = server.Password ?? "";
        }

        internal static string CreateLoginScript(Uri uri, string userId, string passId, string buttonId,
            string username, string password)
        {
            string data = JsonConvert.SerializeObject(new { origin = uri.GetLeftPart(UriPartial.Authority),
                userId, passId, buttonId, username, password },
                new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml });
            // Recheck inside the document in case navigation raced script execution.
            return @"(() => {
                const data = " + data + @";
                if (location.origin !== new URL(data.origin).origin) return false;
                const user = document.getElementById(data.userId);
                const pass = document.getElementById(data.passId);
                const button = document.getElementById(data.buttonId);
                if (!user || !pass || !button) return false;
                if (pass.form && new URL(pass.form.action || location.href, location.href).origin !== location.origin) return false;
                if (button.hasAttribute('formaction') && new URL(button.formAction, location.href).origin !== location.origin) return false;
                const fill = (element, value) => {
                    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
                    if (element instanceof HTMLInputElement) setter.call(element, value || '');
                    else element.value = value || '';
                    element.dispatchEvent(new Event('input', { bubbles: true }));
                    element.dispatchEvent(new Event('change', { bubbles: true }));
                };
                fill(user, data.username);
                fill(pass, data.password);
                button.click();
                return true;
            })()";
        }

        private async void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (closing || !e.IsSuccess || !server.UseHtmlLogin || !tryAutoLogin ||
                !SameOrigin(loginUri, webBrowser.Source?.AbsoluteUri)) return;
            tryAutoLogin = false;
            try
            {
                string result = await webBrowser.CoreWebView2.ExecuteScriptAsync(CreateLoginScript(loginUri,
                    server.HtmlUserBox, server.HtmlPassBox, server.HtmlLoginBtn, server.Username, server.Password));
                if (!closing && !IsDisposed && result != "true") tryAutoLogin = true;
            }
            catch (Exception)
            {
                // Closing or navigating can cancel execution. Manual login remains available.
                // Do not display script errors that could contain credentials.
            }
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            Uri uri;
            if (!closing && Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                webBrowser.CoreWebView2.Navigate(uri.AbsoluteUri);
        }

        private void ShowBrowserError(string message)
        {
            if (closing || IsDisposed) return;
            webBrowser.Visible = false;
            var error = new Label { Text = message, Dock = DockStyle.Fill,
                Padding = new Padding(20), AutoSize = false };
            Controls.Add(error);
            error.BringToFront();
        }

        public bool OnTabClose()
        {
            closing = true;
            webBrowser.Dispose();
            return true;
        }

        public void OnTabFocus()
        {
            if (!closing && !webBrowser.IsDisposed && webBrowser.Visible) webBrowser.Focus();
        }

        private void OnEnter(object sender, EventArgs e) { OnTabFocus(); }
    }
}
