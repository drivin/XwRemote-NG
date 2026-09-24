using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using XwRemote.Servers;

// Run on Windows with WebView2 Evergreen Runtime installed. Only loopback HTTP is used.
internal static class BrowserSmokeTests
{
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static int result = 1;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        using (var window = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-20000, -20000), Size = new System.Drawing.Size(800, 600) })
        {
            window.Shown += async (s, e) =>
            {
                try { await Run(window); result = 0; Console.WriteLine("PASS: all browser smoke tests"); }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { window.Close(); }
            };
            Application.Run(window);
        }
        return result;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static async Task Until(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var attempt = condition();
            if (await Task.WhenAny(attempt, Task.Delay(5000)) != attempt)
                throw new Exception("Browser did not respond: " + message);
            if (await attempt) return;
            await Task.Delay(100);
        }
        throw new Exception("Timeout: " + message);
    }

    private static async Task Run(Form window)
    {
        var addressMethod = typeof(IEForm).GetMethod("GetAddress", PrivateStatic);
        var originMethod = typeof(IEForm).GetMethod("SameOrigin", PrivateStatic);
        var scriptMethod = typeof(IEForm).GetMethod("CreateLoginScript", PrivateStatic);
        var address = (Uri)addressMethod.Invoke(null, new object[] { "localhost:8080/path" });
        Check(address.AbsoluteUri == "http://localhost:8080/path", "host and port normalization");
        foreach (string invalid in new[] { "", "file:///c:/test", "javascript://test", "https://user:pass@example.com" })
        {
            bool rejected = false;
            try { addressMethod.Invoke(null, new object[] { invalid }); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is ArgumentException; }
            Check(rejected, "reject unsupported URL / embedded credentials");
        }
        Check(!(bool)originMethod.Invoke(null, new object[] { new Uri("https://example.com"), "http://example.com" }), "reject HTTPS downgrade for auto-login");
        Check(!(bool)originMethod.Invoke(null, new object[] { new Uri("https://example.com"), "https://example.com:444" }), "reject different port for auto-login");

        using (var site = new TestSite())
        using (var otherSite = new TestSite())
        {
            const string username = "user'\"\\<>&";
            const string password = "pass'\"\\\n<>&";
            var server = new IEServer { Host = site.Url, ID = -987654, Username = username, Password = password,
                UseHtmlLogin = true, HtmlUserBox = "user", HtmlPassBox = "pass", HtmlLoginBtn = "login" };
            using (var tab = new IEForm(server))
            {
                window.Controls.Add(tab);
                tab.Show();
                var view = (WebView2)typeof(IEForm).GetField("webBrowser", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tab);
                await Until(async () => view.CoreWebView2 != null &&
                    await view.ExecuteScriptAsync("window.clicked === true") == "true", "runtime startup and HTML auto-login");
                Check(JsonConvert.DeserializeObject<string>(await view.ExecuteScriptAsync("document.getElementById('user').value")) == username, "username escaping");
                // HTML input elements strip line breaks even when assigned through the native setter.
                Check(JsonConvert.DeserializeObject<string>(await view.ExecuteScriptAsync("document.getElementById('pass').value")) == password.Replace("\n", ""), "password escaping and HTML input normalization");
                Check(await view.ExecuteScriptAsync("window.inputEvents === 2 && window.changeEvents === 2") == "true", "input/change events");
                Check(await view.ExecuteScriptAsync("typeof Promise === 'function' && typeof fetch === 'function' && CSS.supports('display', 'grid')") == "true", "modern JavaScript and CSS");

                string script = (string)scriptMethod.Invoke(null, new object[] { new Uri(site.Url), "user", "pass", "login", username, password });
                await view.ExecuteScriptAsync("document.getElementById('pass').value = ''; document.querySelector('form').action = '" + otherSite.Url + "';");
                Check(await view.ExecuteScriptAsync(script) == "false", "reject cross-origin form action");
                Check(await view.ExecuteScriptAsync("document.getElementById('pass').value === ''") == "true", "do not fill cross-origin form");
                await view.ExecuteScriptAsync("document.querySelector('form').action = location.href; document.getElementById('login').setAttribute('formaction', '" + otherSite.Url + "');");
                Check(await view.ExecuteScriptAsync(script) == "false", "reject cross-origin button action");
                view.CoreWebView2.Navigate(otherSite.Url);
                await Until(async () => await view.ExecuteScriptAsync("location.origin === '" + otherSite.Url.TrimEnd('/') + "' && !!document.getElementById('pass')") == "true", "second origin navigation");
                Check(await view.ExecuteScriptAsync(script) == "false", "script rejects a different origin");
                Check(await view.ExecuteScriptAsync("document.getElementById('pass').value === ''") == "true", "no credentials at other origin");
                tab.OnTabClose();
                Check(view.IsDisposed, "tab closes browser control");
            }
            server.Host = site.Url + "auth";
            server.UseHtmlLogin = false;
            server.Username = "test-user";
            server.Password = "test-password";
            using (var tab = new IEForm(server))
            {
                window.Controls.Add(tab);
                tab.Show();
                var view = (WebView2)typeof(IEForm).GetField("webBrowser", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tab);
                await Until(async () => view.CoreWebView2 != null &&
                    await view.ExecuteScriptAsync("document.body && document.body.textContent === 'authenticated'") == "true", "HTTP authentication");
                Check(site.Authenticated, "HTTP credentials supplied through browser API");
                tab.OnTabClose();
            }
            using (var tab = new IEForm(server))
            {
                window.Controls.Add(tab);
                tab.Show();
                tab.OnTabClose();
                await Task.Delay(200);
                Check(true, "close during initialization");
            }
        }
    }

    private sealed class TestSite : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        internal string Url;
        internal volatile bool Authenticated;
        internal TestSite()
        {
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await listener.AcceptTcpClientAsync();
                        _ = Task.Run(async () =>
                        {
                        using (client)
                        using (var stream = client.GetStream())
                        using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                        {
                            string request = await reader.ReadLineAsync();
                            string header;
                            bool auth = false;
                            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                                if (header.Equals("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("test-user:test-password")), StringComparison.OrdinalIgnoreCase)) auth = true;
                            bool isAuth = request != null && request.StartsWith("GET /auth ");
                            bool challenge = isAuth && !auth;
                            if (isAuth && auth) Authenticated = true;
                            string body = isAuth ? (auth ? "authenticated" : "unauthorized") :
                                "<!doctype html><form onsubmit='return false'><input id='user'><input id='pass' type='password'><button id='login' type='button' onclick='window.clicked=true'>Login</button></form><script>window.inputEvents=0;window.changeEvents=0;document.addEventListener('input',()=>window.inputEvents++);document.addEventListener('change',()=>window.changeEvents++);</script>";
                            byte[] bytes = Encoding.UTF8.GetBytes(body);
                            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + (challenge ? "401 Unauthorized" : "200 OK") + "\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n" +
                                (challenge ? "WWW-Authenticate: Basic realm=\"test\"\r\n" : "") + "Content-Length: " + bytes.Length + "\r\n\r\n");
                            await stream.WriteAsync(response, 0, response.Length);
                            await stream.WriteAsync(bytes, 0, bytes.Length);
                        }
                        });
                    }
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            });
        }
        public void Dispose() { listener.Stop(); }
    }
}
