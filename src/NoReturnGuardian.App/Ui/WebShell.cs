using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>
    /// 承载网页界面的 WebView2。界面是编进 exe 的单个 index.html，只在虚拟源 https://guardian.ui 下提供；
    /// 页面只能用 postMessage 发命令，不能导航到别处，也打不开新窗口。
    /// </summary>
    internal sealed class WebShell : IDisposable
    {
        private const string Origin = "https://guardian.ui/";
        private const string PageResource = "NoReturnGuardian.Ui.index.html";
        private static readonly object LoaderLock = new object();
        private static bool _loaderReady;
        private readonly WebView2 _view;
        private readonly string _devUrl;
        private readonly string _userDataFolder;
        private readonly string _browserArguments;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private byte[] _page;

        public WebShell(string devUrl, string userDataFolder, string browserArguments)
        {
            _devUrl = devUrl;
            _userDataFolder = userDataFolder;
            _browserArguments = browserArguments;
            _view = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(255, 6, 6, 6)
            };
        }

        public Control Control
        {
            get { return _view; }
        }

        /// <summary>页面发来的命令；已解析为 JSON 对象。</summary>
        public event Action<Dictionary<string, object>> Message;

        /// <summary>WebView2 无法启动（运行时缺失、加载器损坏）时的原因。</summary>
        public event Action<string> Failed;

        public bool Initialized { get; private set; }

        /// <summary>页面只能来自这个源：内嵌界面，或开发时指定的本机开发服务器。</summary>
        private string AllowedPrefix
        {
            get { return _devUrl == null ? Origin : new Uri(_devUrl).GetLeftPart(UriPartial.Authority) + "/"; }
        }

        public async Task InitializeAsync()
        {
            try
            {
                PrepareLoader();
                var options = new CoreWebView2EnvironmentOptions(_browserArguments);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                    null, _userDataFolder, options);
                await _view.EnsureCoreWebView2Async(environment);
            }
            catch (Exception error)
            {
                if (Failed != null)
                {
                    Failed(error.Message);
                }

                return;
            }

            CoreWebView2 core = _view.CoreWebView2;
            CoreWebView2Settings settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = _devUrl != null;
            settings.AreBrowserAcceleratorKeysEnabled = _devUrl != null;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            // 页面里标了 app-region: drag 的空白处像标题栏一样拖动窗口、双击最大化。
            settings.IsNonClientRegionSupportEnabled = true;

            core.NewWindowRequested += (sender, args) => args.Handled = true;
            core.NavigationStarting += (sender, args) =>
            {
                if (!args.Uri.StartsWith(AllowedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                }
            };
            core.WebMessageReceived += HandleWebMessage;

            if (_devUrl == null)
            {
                _page = ReadResource(PageResource);
                core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += ServePage;
                core.Navigate(Origin + "index.html");
            }
            else
            {
                core.Navigate(_devUrl);
            }

            Initialized = true;
        }

        private void ServePage(object sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            Uri uri = new Uri(args.Request.Uri);
            bool page = uri.AbsolutePath == "/" || uri.AbsolutePath == "/index.html";
            args.Response = _view.CoreWebView2.Environment.CreateWebResourceResponse(
                page ? new MemoryStream(_page, false) : new MemoryStream(new byte[0]),
                page ? 200 : 404,
                page ? "OK" : "Not Found",
                page
                    ? "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n"
                        + "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:"
                    : "Content-Type: text/plain");
        }

        private void HandleWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            if (!args.Source.StartsWith(AllowedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Dictionary<string, object> message;
            try
            {
                message = _serializer.Deserialize<Dictionary<string, object>>(args.WebMessageAsJson);
            }
            catch (ArgumentException)
            {
                return;
            }

            if (message != null && Message != null)
            {
                Message(message);
            }
        }

        public void Post(object message)
        {
            PostJson(_serializer.Serialize(message));
        }

        public void PostJson(string json)
        {
            if (Initialized && _view.CoreWebView2 != null)
            {
                _view.CoreWebView2.PostWebMessageAsJson(json);
            }
        }

        public string Serialize(object value)
        {
            return _serializer.Serialize(value);
        }

        public Task<string> ExecuteScriptAsync(string script)
        {
            return _view.CoreWebView2.ExecuteScriptAsync(script);
        }

        public async Task CaptureAsync(string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            }
        }

        /// <summary>
        /// 窗口藏起或最小化时把浏览器设为不可见：页面收到 visibilitychange，色场停帧。
        /// 要在父窗口隐藏之前调用——父窗口已经隐藏时，子控件的 Visible 读出来已是 false，设置不会再触发通知。
        /// </summary>
        public void SetVisible(bool visible)
        {
            _view.Visible = visible;
        }

        /// <summary>窗口藏到托盘后让页面休眠：脚本与光影动画全部停下，重新显示时自动恢复。</summary>
        public async void Suspend()
        {
            if (!Initialized || _view.CoreWebView2 == null || _view.Visible)
            {
                return;
            }

            try
            {
                await _view.CoreWebView2.TrySuspendAsync();
            }
            catch (Exception error) when (error is InvalidOperationException || error is COMException)
            {
                // 休眠只是省资源；做不到时页面照常留在后台。
            }
        }

        public void Dispose()
        {
            _view.Dispose();
        }

        /// <summary>
        /// WebView2Loader.dll 按位数编进 exe；首次运行解到本地数据目录，按内容哈希分目录，旧文件被占用也不冲突。
        /// </summary>
        private static void PrepareLoader()
        {
            lock (LoaderLock)
            {
                if (_loaderReady)
                {
                    return;
                }

                string architecture = RuntimeArchitecture();
                byte[] loader = ReadResource("NoReturnGuardian.WebView2Loader." + architecture + ".dll");
                string hash;
                using (SHA256 sha = SHA256.Create())
                {
                    hash = BitConverter.ToString(sha.ComputeHash(loader), 0, 6).Replace("-", "").ToLowerInvariant();
                }

                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NoReturnGuardian",
                    "webview2-loader",
                    architecture + "-" + hash);
                string path = Path.Combine(folder, "WebView2Loader.dll");
                if (!File.Exists(path) || new FileInfo(path).Length != loader.Length)
                {
                    Directory.CreateDirectory(folder);
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temporary, loader);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    File.Move(temporary, path);
                }

                CoreWebView2Environment.SetLoaderDllFolderPath(folder);
                _loaderReady = true;
            }
        }

        private static string RuntimeArchitecture()
        {
            string processor = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            if (string.Equals(processor, "ARM64", StringComparison.OrdinalIgnoreCase))
            {
                return Environment.Is64BitProcess ? "arm64" : "x86";
            }

            return Environment.Is64BitProcess ? "x64" : "x86";
        }

        internal static byte[] ReadResource(string name)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException("界面资源缺失：" + name);
                }

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }
    }
}
