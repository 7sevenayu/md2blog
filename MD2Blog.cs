// MD2Blog.cs — 拖入 Markdown 发布 WordPress 草稿（支持特色图）+ 草稿特色图管理器
// 编译: csc /nologo /target:winexe /out:MD2Blog.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll MD2Blog.cs
// 用法:
//   GUI: 拖入 .md（可配图）→ 发布草稿；点「管理草稿特色图」→ 列表/预览/更换/清除
//   CMD: MD2Blog.exe 文件.md [封面.png] [-quiet]
// 配置: 同目录 blog-config.json（DPAPI 加密存储）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

public class BlogConfig
{
    public string site = "";
    public string user = "";
    public string password = "";
    public string status = "draft";
}

public class DraftInfo
{
    public string PostId = "";
    public string Title = "";
    public bool HasThumb;
    public string ThumbAttachId = "";
    public string ThumbUrl = "";
}

// 配置存取：用 Windows DPAPI 加密保存（仅本机当前用户可解密），兼容旧的明文格式
public static class ConfigStore
{
    public static string ConfigPath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blog-config.json"); }
    }

    public static BlogConfig Load()
    {
        var cfg = new BlogConfig();
        if (!File.Exists(ConfigPath)) return cfg;
        string json = null;
        try
        {
            byte[] enc = File.ReadAllBytes(ConfigPath);
            json = Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            try { json = File.ReadAllText(ConfigPath, Encoding.UTF8); } catch { json = null; }
        }
        if (json != null)
        {
            cfg.site = Get(json, "site", cfg.site);
            cfg.user = Get(json, "user", cfg.user);
            cfg.password = Get(json, "password", cfg.password);
            cfg.status = Get(json, "status", cfg.status);
        }
        return cfg;
    }

    public static void Save(BlogConfig cfg)
    {
        string json = "{\"site\":\"" + JsonEsc(cfg.site) + "\",\"user\":\"" + JsonEsc(cfg.user) +
                      "\",\"password\":\"" + JsonEsc(cfg.password) + "\",\"status\":\"" + JsonEsc(cfg.status) + "\"}";
        byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(ConfigPath, enc);
    }

    static string JsonEsc(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\""); }

    static string Get(string json, string key, string def)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : def;
    }
}

public static class MdToHtml
{
    static string Token() { return "TKN" + Guid.NewGuid().ToString("N") + "TKN"; }

    public static string EscapeHtml(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    public static string EscapeXml(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    public static string Unescape(string s)
    {
        return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                .Replace("&apos;", "'").Replace("&amp;", "&");
    }

    public static string Inline(string s)
    {
        var tokens = new Dictionary<string, string>();
        s = Regex.Replace(s, @"`([^`]+)`", m => {
            var k = Token(); tokens[k] = "<code>" + EscapeHtml(m.Groups[1].Value) + "</code>"; return k;
        });
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", m => {
            var k = Token(); tokens[k] = "<strong>" + EscapeHtml(m.Groups[1].Value) + "</strong>"; return k;
        });
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", m => {
            var k = Token();
            tokens[k] = "<a href=\"" + EscapeHtml(m.Groups[2].Value) + "\">" + EscapeHtml(m.Groups[1].Value) + "</a>";
            return k;
        });
        s = EscapeHtml(s);
        foreach (var kv in tokens) s = s.Replace(kv.Key, kv.Value);
        return s;
    }

    static bool IsBlockStart(string t)
    {
        return Regex.IsMatch(t, @"^(```|\||>|[-*]\s|\d+\.\s|#{1,6}\s|-{3,}$)");
    }

    static string[] SplitRow(string row)
    {
        var cells = row.Trim().Trim('|').Split('|');
        var res = new List<string>();
        foreach (var c in cells) res.Add(c.Trim());
        return res.ToArray();
    }

    public static string Convert(string[] lines, out string title)
    {
        title = "";
        int start = 0;
        if (lines.Length > 0)
        {
            var m0 = Regex.Match(lines[0].Trim(), @"^#\s+(.+)$");
            if (m0.Success) { title = m0.Groups[1].Value.Trim(); start = 1; }
        }
        var sb = new StringBuilder();
        int i = start, n = lines.Length;
        while (i < n)
        {
            string trim = lines[i].Trim();
            if (trim.Length == 0) { i++; continue; }

            if (trim.StartsWith("```"))
            {
                i++;
                var code = new List<string>();
                while (i < n && !lines[i].TrimStart().StartsWith("```")) { code.Add(EscapeHtml(lines[i])); i++; }
                i++;
                sb.Append("<pre><code>").Append(string.Join("\n", code)).Append("</code></pre>");
                continue;
            }
            if (trim.StartsWith("|"))
            {
                var rows = new List<string>();
                while (i < n && lines[i].Trim().StartsWith("|")) { rows.Add(lines[i].Trim()); i++; }
                if (rows.Count >= 2)
                {
                    sb.Append("<table><thead><tr>");
                    foreach (var h in SplitRow(rows[0])) sb.Append("<th>").Append(Inline(h)).Append("</th>");
                    sb.Append("</tr></thead>");
                    if (rows.Count > 2)
                    {
                        sb.Append("<tbody>");
                        for (int r = 2; r < rows.Count; r++)
                        {
                            sb.Append("<tr>");
                            foreach (var c in SplitRow(rows[r])) sb.Append("<td>").Append(Inline(c)).Append("</td>");
                            sb.Append("</tr>");
                        }
                        sb.Append("</tbody>");
                    }
                    sb.Append("</table>");
                }
                continue;
            }
            if (trim.StartsWith(">"))
            {
                var q = new List<string>();
                while (i < n && lines[i].Trim().StartsWith(">")) { q.Add(Regex.Replace(lines[i].Trim(), @"^>\s?", "")); i++; }
                sb.Append("<blockquote><p>").Append(Inline(string.Join(" ", q))).Append("</p></blockquote>");
                continue;
            }
            if (Regex.IsMatch(trim, @"^[-*]\s+"))
            {
                var items = new List<string>();
                while (i < n && Regex.IsMatch(lines[i].Trim(), @"^[-*]\s+")) { items.Add(Regex.Replace(lines[i].Trim(), @"^[-*]\s+", "")); i++; }
                sb.Append("<ul>");
                foreach (var it in items) sb.Append("<li>").Append(Inline(it)).Append("</li>");
                sb.Append("</ul>");
                continue;
            }
            if (Regex.IsMatch(trim, @"^\d+\.\s+"))
            {
                var items = new List<string>();
                while (i < n && Regex.IsMatch(lines[i].Trim(), @"^\d+\.\s+")) { items.Add(Regex.Replace(lines[i].Trim(), @"^\d+\.\s+", "")); i++; }
                sb.Append("<ol>");
                foreach (var it in items) sb.Append("<li>").Append(Inline(it)).Append("</li>");
                sb.Append("</ol>");
                continue;
            }
            var hm = Regex.Match(trim, @"^(#{1,6})\s+(.+)$");
            if (hm.Success)
            {
                int level = hm.Groups[1].Value.Length;
                sb.Append("<h").Append(level).Append(">").Append(Inline(hm.Groups[2].Value.Trim()))
                  .Append("</h").Append(level).Append(">");
                i++;
                continue;
            }
            if (Regex.IsMatch(trim, @"^-{3,}$")) { sb.Append("<hr>"); i++; continue; }

            var paras = new List<string>();
            while (i < n && lines[i].Trim().Length > 0 && !IsBlockStart(lines[i].Trim())) { paras.Add(lines[i].Trim()); i++; }
            sb.Append("<p>").Append(Inline(string.Join(" ", paras))).Append("</p>");
        }
        return sb.ToString();
    }
}

public static class Blogger
{
    static string Call(BlogConfig cfg, string methodXml)
    {
        string url = cfg.site.TrimEnd('/') + "/xmlrpc.php";
        using (var wc = new WebClient())
        {
            wc.Headers[HttpRequestHeader.ContentType] = "text/xml";
            byte[] respBytes = wc.UploadData(url, "POST", Encoding.UTF8.GetBytes(methodXml));
            return Encoding.UTF8.GetString(respBytes);
        }
    }

    public static Task<string> CallAsync(BlogConfig cfg, string methodXml)
    {
        return Task.Run(() => Call(cfg, methodXml));
    }

    static string NewPostXml(BlogConfig cfg, string title, string content)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\"?>");
        xml.Append("<methodCall><methodName>wp.newPost</methodName><params>");
        xml.Append("<param><value><int>1</int></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.user)).Append("</string></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.password)).Append("</string></value></param>");
        xml.Append("<param><value><struct>");
        xml.Append("<member><name>post_type</name><value><string>post</string></value></member>");
        xml.Append("<member><name>post_status</name><value><string>").Append(MdToHtml.EscapeXml(cfg.status)).Append("</string></value></member>");
        xml.Append("<member><name>post_title</name><value><string>").Append(MdToHtml.EscapeXml(title)).Append("</string></value></member>");
        xml.Append("<member><name>post_content</name><value><string>").Append(MdToHtml.EscapeXml(content)).Append("</string></value></member>");
        xml.Append("</struct></value></param>");
        xml.Append("</params></methodCall>");
        return xml.ToString();
    }

    static string GetPostsXml(BlogConfig cfg)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\"?>");
        xml.Append("<methodCall><methodName>wp.getPosts</methodName><params>");
        xml.Append("<param><value><int>1</int></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.user)).Append("</string></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.password)).Append("</string></value></param>");
        xml.Append("<param><value><array><data></data></array></value></param>");
        xml.Append("<param><value><struct>");
        xml.Append("<member><name>post_type</name><value><string>post</string></value></member>");
        xml.Append("<member><name>post_status</name><value><string>").Append(MdToHtml.EscapeXml(cfg.status)).Append("</string></value></member>");
        xml.Append("<member><name>number</name><value><int>100</int></value></member>");
        xml.Append("<member><name>orderby</name><value><string>date</string></value></member>");
        xml.Append("<member><name>order</name><value><string>desc</string></value></member>");
        xml.Append("</struct></value></param>");
        xml.Append("</params></methodCall>");
        return xml.ToString();
    }

    static string GetPostXml(BlogConfig cfg, string postId)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\"?>");
        xml.Append("<methodCall><methodName>wp.getPost</methodName><params>");
        xml.Append("<param><value><int>1</int></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.user)).Append("</string></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.password)).Append("</string></value></param>");
        xml.Append("<param><value><int>").Append(postId).Append("</int></value></param>");
        xml.Append("</params></methodCall>");
        return xml.ToString();
    }

    static string UploadImageXml(BlogConfig cfg, string path, out string mime)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        mime = "image/png";
        switch (ext)
        {
            case ".jpg": case ".jpeg": mime = "image/jpeg"; break;
            case ".gif": mime = "image/gif"; break;
            case ".webp": mime = "image/webp"; break;
            case ".bmp": mime = "image/bmp"; break;
        }
        string b64 = Convert.ToBase64String(File.ReadAllBytes(path));
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\"?>");
        xml.Append("<methodCall><methodName>wp.uploadFile</methodName><params>");
        xml.Append("<param><value><int>1</int></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.user)).Append("</string></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.password)).Append("</string></value></param>");
        xml.Append("<param><value><struct>");
        xml.Append("<member><name>name</name><value><string>").Append(MdToHtml.EscapeXml(Path.GetFileName(path))).Append("</string></value></member>");
        xml.Append("<member><name>type</name><value><string>").Append(mime).Append("</string></value></member>");
        xml.Append("<member><name>bits</name><value><base64>").Append(b64).Append("</base64></value></member>");
        xml.Append("<member><name>overwrite</name><value><boolean>0</boolean></value></member>");
        xml.Append("</struct></value></param>");
        xml.Append("</params></methodCall>");
        return xml.ToString();
    }

    // attachId 为空字符串 = 清除特色图
    static string EditPostThumbnailXml(BlogConfig cfg, string postId, string attachId)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\"?>");
        xml.Append("<methodCall><methodName>wp.editPost</methodName><params>");
        xml.Append("<param><value><int>1</int></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.user)).Append("</string></value></param>");
        xml.Append("<param><value><string>").Append(MdToHtml.EscapeXml(cfg.password)).Append("</string></value></param>");
        xml.Append("<param><value><int>").Append(postId).Append("</int></value></param>");
        xml.Append("<param><value><struct>");
        if (attachId.Length == 0)
            xml.Append("<member><name>post_thumbnail</name><value><string></string></value></member>");
        else
            xml.Append("<member><name>post_thumbnail</name><value><int>").Append(attachId).Append("</int></value></member>");
        xml.Append("</struct></value></param>");
        xml.Append("</params></methodCall>");
        return xml.ToString();
    }

    static string FaultOf(string resp)
    {
        var fc = Regex.Match(resp, @"faultCode[^>]*>\s*<int>(\d+)</int>");
        if (!fc.Success) return "";
        var fs = Regex.Match(resp, @"faultString[^>]*>\s*<string>([\s\S]*?)</string>");
        return "faultCode=" + fc.Groups[1].Value + ": " + (fs.Success ? fs.Groups[1].Value : "(无详情)");
    }

    static DraftInfo ParseDetail(string postId, string resp)
    {
        var d = new DraftInfo { PostId = postId };
        var t = Regex.Match(resp, @"<name>post_title</name><value><string>([\s\S]*?)</string>");
        d.Title = t.Success ? MdToHtml.Unescape(t.Groups[1].Value) : "(无标题)";
        int idx = resp.IndexOf("post_thumbnail");
        if (idx >= 0)
        {
            string seg = resp.Substring(idx, Math.Min(700, resp.Length - idx));
            var aid = Regex.Match(seg, @"<name>attachment_id</name><value><string>(\d+)</string>");
            var lnk = Regex.Match(seg, @"<name>link</name><value><string>([^<]*)</string>");
            d.HasThumb = aid.Success;
            d.ThumbAttachId = aid.Success ? aid.Groups[1].Value : "";
            d.ThumbUrl = lnk.Success ? MdToHtml.Unescape(lnk.Groups[1].Value) : "";
        }
        return d;
    }

    // ---------- 发布文章（可选特色图） ----------
    public static string Publish(BlogConfig cfg, string mdPath, string imagePath, out string title, out string postId, out string thumbInfo)
    {
        title = ""; postId = ""; thumbInfo = "";
        if (!File.Exists(mdPath)) return "文件不存在: " + mdPath;
        if (cfg.user.Length == 0 || cfg.password.Length == 0) return "未配置用户名/密码（请先运行一次配置）";

        string[] lines = File.ReadAllLines(mdPath, Encoding.UTF8);
        string content = MdToHtml.Convert(lines, out title);
        if (title.Length == 0) title = Path.GetFileNameWithoutExtension(mdPath);

        try
        {
            string resp = Call(cfg, NewPostXml(cfg, title, content));
            string fault = FaultOf(resp);
            if (fault.Length > 0) return "创建文章失败: " + fault;
            var idm = Regex.Match(resp, @"<string>(\d+)</string>");
            if (!idm.Success) return "创建文章失败: 无法解析响应";
            postId = idm.Groups[1].Value;

            if (imagePath != null && File.Exists(imagePath))
            {
                string mime;
                string upResp = Call(cfg, UploadImageXml(cfg, imagePath, out mime));
                string upFault = FaultOf(upResp);
                if (upFault.Length > 0) return "文章已创建(ID " + postId + ")，但上传特色图失败: " + upFault;
                var aid = Regex.Match(upResp, @"<name>id</name><value><string>(\d+)</string>");
                var aid2 = Regex.Match(upResp, @"<name>id</name><value><int>(\d+)</int>");
                string attachId = aid.Success ? aid.Groups[1].Value : (aid2.Success ? aid2.Groups[1].Value : "");
                if (attachId.Length == 0) return "文章已创建(ID " + postId + ")，但无法解析图片上传响应";
                string thResp = Call(cfg, EditPostThumbnailXml(cfg, postId, attachId));
                string thFault = FaultOf(thResp);
                if (thFault.Length > 0) return "文章已创建(ID " + postId + ")，图片已上传但设置特色图失败: " + thFault;
                if (!thResp.Contains("<boolean>1</boolean>")) return "文章已创建(ID " + postId + ")，图片已上传但设置特色图失败（服务器返回 false）";
                thumbInfo = "特色图: " + Path.GetFileName(imagePath) + " (附件ID " + attachId + ")";
            }
            return "";
        }
        catch (Exception ex) { return "网络/请求错误: " + ex.Message; }
    }

    // ---------- 异步 API（UI 用，不阻塞界面） ----------
    public static async Task<List<DraftInfo>> ListDraftsAsync(BlogConfig cfg)
    {
        string resp = await CallAsync(cfg, GetPostsXml(cfg));
        string fault = FaultOf(resp);
        if (fault.Length > 0) throw new Exception(fault);
        var ids = Regex.Matches(resp, @"<name>post_id</name><value><string>(\d+)</string>");
        var tasks = new List<Task<DraftInfo>>();
        foreach (Match m in ids)
            tasks.Add(GetDraftDetailAsync(cfg, m.Groups[1].Value));
        var results = await Task.WhenAll(tasks);
        var list = new List<DraftInfo>();
        foreach (var r in results) if (r != null) list.Add(r);
        return list;
    }

    public static async Task<DraftInfo> GetDraftDetailAsync(BlogConfig cfg, string postId)
    {
        try
        {
            string resp = await CallAsync(cfg, GetPostXml(cfg, postId));
            string fault = FaultOf(resp);
            if (fault.Length > 0) return null;
            return ParseDetail(postId, resp);
        }
        catch { return null; }
    }

    public class ThumbResult { public string Error = ""; public string AttachId = ""; }

    public static async Task<ThumbResult> SetThumbnailAsync(BlogConfig cfg, string postId, string imagePath)
    {
        var r = new ThumbResult();
        try
        {
            string mime;
            string upResp = await CallAsync(cfg, UploadImageXml(cfg, imagePath, out mime));
            string upFault = FaultOf(upResp);
            if (upFault.Length > 0) { r.Error = "上传图片失败: " + upFault; return r; }
            var aid = Regex.Match(upResp, @"<name>id</name><value><string>(\d+)</string>");
            var aid2 = Regex.Match(upResp, @"<name>id</name><value><int>(\d+)</int>");
            r.AttachId = aid.Success ? aid.Groups[1].Value : (aid2.Success ? aid2.Groups[1].Value : "");
            if (r.AttachId.Length == 0) { r.Error = "无法解析上传响应"; return r; }
            string thResp = await CallAsync(cfg, EditPostThumbnailXml(cfg, postId, r.AttachId));
            string thFault = FaultOf(thResp);
            if (thFault.Length > 0) { r.Error = "设置特色图失败: " + thFault; return r; }
            if (!thResp.Contains("<boolean>1</boolean>")) { r.Error = "设置特色图失败（服务器返回 false）"; return r; }
            return r;
        }
        catch (Exception ex) { r.Error = "网络/请求错误: " + ex.Message; return r; }
    }

    public static async Task<string> ClearThumbnailAsync(BlogConfig cfg, string postId)
    {
        try
        {
            string thResp = await CallAsync(cfg, EditPostThumbnailXml(cfg, postId, ""));
            string thFault = FaultOf(thResp);
            if (thFault.Length > 0) return "清除特色图失败: " + thFault;
            if (!thResp.Contains("<boolean>1</boolean>")) return "清除特色图失败（服务器返回 false）";
            return "";
        }
        catch (Exception ex) { return "网络/请求错误: " + ex.Message; }
    }

    public static async Task<Image> DownloadImageAsync(string url)
    {
        byte[] data = await Task.Run(() =>
        {
            using (var wc = new WebClient()) return wc.DownloadData(url);
        });
        using (var ms = new MemoryStream(data))
        using (var img = Image.FromStream(ms))
            return new Bitmap(img);
    }
}

public class ConfigForm : Form
{
    TextBox tSite, tUser, tPass, tStatus;
    Label testResult;
    Button btnTest;
    public BlogConfig Result;

    public ConfigForm(BlogConfig cur)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "登录到你的 WordPress 博客";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 320);

        var title = new Label { Text = "MD2Blog 登录设置", Font = new Font("Microsoft YaHei", 14, FontStyle.Bold), Location = new Point(15, 12), AutoSize = true };
        var desc = new Label { Text = "填写你的 WordPress 博客信息，用于发布文章与草稿管理", Font = new Font("Microsoft YaHei", 9), Location = new Point(15, 46), AutoSize = true, ForeColor = Color.Gray };
        Controls.Add(title);
        Controls.Add(desc);

        int y = 82;
        tSite = AddRow("站点 URL:", cur.site, ref y, false, "https://你的博客地址");
        tUser = AddRow("用户名:", cur.user, ref y);
        tPass = AddRow("密码:", cur.password, ref y, true);
        tStatus = AddRow("发布状态:", cur.status, ref y, false, "draft（草稿）/ publish（直接发布）");

        testResult = new Label { Text = "", Location = new Point(140, y + 6), AutoSize = true, ForeColor = Color.Gray };
        Controls.Add(testResult);

        btnTest = new Button { Text = "测试连接", Location = new Point(15, y + 2), Size = new Size(110, 32) };
        btnTest.Click += (s, e) => TestConnection();
        Controls.Add(btnTest);

        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Location = new Point(230, y + 2), Size = new Size(120, 32) };
        ok.Click += (s, e) => Save();
        Controls.Add(ok);
        AcceptButton = ok;
    }

    TextBox AddRow(string label, string value, ref int y, bool password = false, string placeholder = "")
    {
        Controls.Add(new Label { Text = label, Location = new Point(15, y + 6), AutoSize = true });
        var tb = new TextBox { Text = value, Location = new Point(140, y), Width = 330 };
        if (password) tb.UseSystemPasswordChar = true;
        if (placeholder.Length > 0 && value.Length == 0)
        {
            tb.ForeColor = Color.Gray;
            tb.Text = placeholder;
            tb.GotFocus += (s, e) => { if (tb.Text == placeholder) { tb.Text = ""; tb.ForeColor = Color.Black; } };
            tb.LostFocus += (s, e) => { if (tb.Text.Length == 0) { tb.Text = placeholder; tb.ForeColor = Color.Gray; } };
        }
        Controls.Add(tb);
        y += 36;
        return tb;
    }

    async void TestConnection()
    {
        string site = tSite.Text.Trim();
        string user = tUser.Text.Trim();
        string pass = tPass.Text;
        if (site.Length == 0 || user.Length == 0 || pass.Length == 0)
        {
            testResult.Text = "✗ 请先填写完整信息";
            testResult.ForeColor = Color.Red;
            return;
        }
        btnTest.Enabled = false;
        testResult.Text = "连接中...";
        testResult.ForeColor = Color.Gray;
        try
        {
            var cfg = new BlogConfig { site = site, user = user, password = pass };
            string resp = await Blogger.CallAsync(cfg,
                "<?xml version=\"1.0\"?><methodCall><methodName>wp.getUsersBlogs</methodName><params>" +
                "<param><value><string>" + MdToHtml.EscapeXml(user) + "</string></value></param>" +
                "<param><value><string>" + MdToHtml.EscapeXml(pass) + "</string></value></param>" +
                "</params></methodCall>");
            if (resp.Contains("<fault>"))
            {
                testResult.Text = "✗ 凭据无效或站点不可达";
                testResult.ForeColor = Color.Red;
            }
            else
            {
                testResult.Text = "✓ 连接成功，凭据有效";
                testResult.ForeColor = Color.Green;
            }
        }
        catch (Exception ex)
        {
            testResult.Text = "✗ 连接失败: " + ex.Message;
            testResult.ForeColor = Color.Red;
        }
        finally { btnTest.Enabled = true; }
    }

    void Save()
    {
        Result = new BlogConfig();
        Result.site = tSite.Text.Trim();
        Result.user = tUser.Text.Trim();
        Result.password = tPass.Text;
        string st = tStatus.Text.Trim().ToLowerInvariant();
        Result.status = (st == "publish") ? "publish" : "draft";
    }
}

public class DraftManagerForm : Form
{
    BlogConfig cfg;
    ListView list;
    PictureBox preview;
    Label info;
    Label status;
    Button btnChange, btnClear, btnRefresh;
    List<DraftInfo> drafts = new List<DraftInfo>();
    bool busy;
    int detailSeq;

    public DraftManagerForm(BlogConfig cfg)
    {
        this.cfg = cfg;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "草稿与特色图管理";
        Size = new Size(820, 560);
        MinimumSize = new Size(700, 440);
        StartPosition = FormStartPosition.CenterScreen;

        // 底部横条：返回主窗口 + 状态（放在列表下方，不遮挡内容）
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var btnBack = new Button { Text = "← 返回主窗口", Location = new Point(8, 7), Size = new Size(150, 30), FlatStyle = FlatStyle.System };
        btnBack.Click += (s, e) => { Close(); };
        bottomPanel.Controls.Add(btnBack);
        status = new Label { Text = "就绪", Location = new Point(170, 13), AutoSize = true, ForeColor = Color.Gray };
        bottomPanel.Controls.Add(status);
        Controls.Add(bottomPanel);

        var right = new Panel { Dock = DockStyle.Right, Width = 300 };
        preview = new PictureBox { Dock = DockStyle.Top, Height = 220, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(240, 240, 240), BorderStyle = BorderStyle.FixedSingle };
        info = new Label { Dock = DockStyle.Fill, Padding = new Padding(6), AutoEllipsis = true, Text = "在左侧选择一篇草稿" };
        var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 96 };
        btnChange = new Button { Text = "更换特色图...", Location = new Point(10, 8), Size = new Size(130, 34) };
        btnClear = new Button { Text = "清除特色图", Location = new Point(150, 8), Size = new Size(130, 34) };
        btnRefresh = new Button { Text = "刷新列表", Location = new Point(10, 50), Size = new Size(270, 32) };
        btnPanel.Controls.AddRange(new Control[] { btnChange, btnClear, btnRefresh });
        right.Controls.Add(info);
        right.Controls.Add(preview);
        right.Controls.Add(btnPanel);
        Controls.Add(right);

        // 列表：占满剩余空间，草稿多时自动出现滚动条（支持滚轮上下滑动）
        list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, Scrollable = true };
        list.Columns.Add("ID", 60);
        list.Columns.Add("标题", 300);
        list.Columns.Add("特色图", 70);
        Controls.Add(list);

        list.SelectedIndexChanged += (s, e) => ShowDetail();
        btnChange.Click += (s, e) => ChangeThumb();
        btnClear.Click += (s, e) => ClearThumb();
        btnRefresh.Click += (s, e) => LoadDrafts();

        LoadDrafts();
    }

    async void LoadDrafts()
    {
        if (busy) return;
        busy = true;
        btnRefresh.Enabled = false;
        btnChange.Enabled = false;
        btnClear.Enabled = false;
        status.Text = "正在加载草稿列表...";
        list.BeginUpdate();
        list.Items.Clear();
        list.EndUpdate();
        preview.Image = null;
        info.Text = "加载中...";
        drafts.Clear();
        try
        {
            drafts = await Blogger.ListDraftsAsync(cfg);
            list.BeginUpdate();
            foreach (var d in drafts)
            {
                var item = new ListViewItem(d.PostId);
                item.SubItems.Add(d.Title);
                item.SubItems.Add(d.HasThumb ? "✓ " + d.ThumbAttachId : "—");
                list.Items.Add(item);
            }
            list.EndUpdate();
            status.Text = "共 " + drafts.Count + " 篇草稿（加载完成）";
            if (drafts.Count > 0) { list.Items[0].Selected = true; }
            else info.Text = "没有草稿";
        }
        catch (Exception ex)
        {
            status.Text = "加载失败";
            info.Text = "加载草稿失败:\n" + ex.Message;
        }
        finally
        {
            busy = false;
            btnRefresh.Enabled = true;
            btnChange.Enabled = true;
            btnClear.Enabled = true;
        }
    }

    async void ShowDetail()
    {
        if (list.SelectedIndices.Count == 0) return;
        int idx = list.SelectedIndices[0];
        if (idx < 0 || idx >= drafts.Count) return;
        var d = drafts[idx];
        int mySeq = ++detailSeq;
        info.Text = "标题: " + d.Title + "\n\n" +
                    (d.HasThumb ? "特色图附件ID: " + d.ThumbAttachId + "\nURL: " + d.ThumbUrl
                                : "当前无特色图");
        preview.Image = null;
        if (d.HasThumb && d.ThumbUrl.Length > 0)
        {
            try
            {
                var img = await Blogger.DownloadImageAsync(d.ThumbUrl);
                if (mySeq == detailSeq) preview.Image = img;
                else img.Dispose();
            }
            catch
            {
                if (mySeq == detailSeq) info.Text += "\n\n(预览图片下载失败，可在浏览器打开上方 URL 查看)";
            }
        }
    }

    async void ChangeThumb()
    {
        if (busy) return;
        if (list.SelectedIndices.Count == 0) { MessageBox.Show("请先选择一篇草稿", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        int idx = list.SelectedIndices[0];
        if (idx < 0 || idx >= drafts.Count) return;
        var d = drafts[idx];
        using (var ofd = new OpenFileDialog())
        {
            ofd.Title = "选择新的特色图（针对草稿 ID " + d.PostId + "）";
            ofd.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|所有文件|*.*";
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            busy = true;
            btnChange.Enabled = false;
            btnClear.Enabled = false;
            btnRefresh.Enabled = false;
            status.Text = "正在上传并设置特色图（请稍候）...";
            Blogger.ThumbResult tr = await Blogger.SetThumbnailAsync(cfg, d.PostId, ofd.FileName);
            busy = false;
            btnChange.Enabled = true;
            btnClear.Enabled = true;
            btnRefresh.Enabled = true;
            if (tr.Error.Length == 0)
            {
                status.Text = "特色图已更新 (附件 " + tr.AttachId + ")";
                MessageBox.Show("草稿 ID " + d.PostId + " 的特色图已更新为:\n" + Path.GetFileName(ofd.FileName) +
                    "\n附件ID: " + tr.AttachId, "✓ 更新成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                drafts[idx] = await Blogger.GetDraftDetailAsync(cfg, d.PostId) ?? drafts[idx];
                RefreshListItem(idx);
                ShowDetail();
            }
            else
            {
                status.Text = "更新失败";
                MessageBox.Show(tr.Error, "✗ 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    async void ClearThumb()
    {
        if (busy) return;
        if (list.SelectedIndices.Count == 0) { MessageBox.Show("请先选择一篇草稿", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        int idx = list.SelectedIndices[0];
        if (idx < 0 || idx >= drafts.Count) return;
        var d = drafts[idx];
        if (!d.HasThumb) { MessageBox.Show("这篇草稿本来就没有特色图", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (MessageBox.Show("确定清除草稿 ID " + d.PostId + " 的特色图吗？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        busy = true;
        btnChange.Enabled = false;
        btnClear.Enabled = false;
        btnRefresh.Enabled = false;
        status.Text = "正在清除特色图...";
        string err = await Blogger.ClearThumbnailAsync(cfg, d.PostId);
        busy = false;
        btnChange.Enabled = true;
        btnClear.Enabled = true;
        btnRefresh.Enabled = true;
        if (err.Length == 0)
        {
            status.Text = "特色图已清除";
            drafts[idx] = await Blogger.GetDraftDetailAsync(cfg, d.PostId) ?? drafts[idx];
            RefreshListItem(idx);
            ShowDetail();
        }
        else
        {
            status.Text = "清除失败";
            MessageBox.Show(err, "✗ 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void RefreshListItem(int idx)
    {
        var d = drafts[idx];
        var item = list.Items[idx];
        item.Text = d.PostId;
        item.SubItems[1].Text = d.Title;
        item.SubItems[2].Text = d.HasThumb ? "✓ " + d.ThumbAttachId : "—";
    }
}

public class MainForm : Form
{
    BlogConfig cfg;
    Label status;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Text = "MD → 博客草稿（拖入 .md，可加图片作特色图）";
        ClientSize = new Size(500, 240);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        var hint = new Label
        {
            Text = "把 Markdown 文件拖到这里\r\n自动发布为 WordPress 草稿\r\n\r\n同时拖入 .png/.jpg 等图片，自动设为特色图\r\n支持一次拖入多篇（第 N 篇配第 N 张图）",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei", 12)
        };
        Controls.Add(hint);
        status = new Label { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray, Text = "就绪" };
        Controls.Add(status);
        var btnManage = new Button { Text = "📝 管理草稿特色图（列表/预览/更换/清除）", Dock = DockStyle.Bottom, Height = 36 };
        btnManage.Click += (s, e) =>
        {
            using (var f = new DraftManagerForm(cfg)) { f.ShowDialog(this); }
        };
        Controls.Add(btnManage);

        DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (s, e) =>
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var mds = new List<string>();
            var imgs = new List<string>();
            foreach (var f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".md" || ext == ".markdown") mds.Add(f);
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".bmp") imgs.Add(f);
            }
            if (mds.Count == 0) { MessageBox.Show("没有检测到 .md / .markdown 文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            int okCount = 0, failCount = 0;
            for (int k = 0; k < mds.Count; k++)
            {
                string img = k < imgs.Count ? imgs[k] : null;
                string title, postId, thumbInfo, err;
                err = Blogger.Publish(cfg, mds[k], img, out title, out postId, out thumbInfo);
                if (err.Length == 0)
                {
                    okCount++;
                    MessageBox.Show("发布成功！\n\n标题: " + title + "\n文章 ID: " + postId + "\n状态: " + cfg.status +
                        (thumbInfo.Length > 0 ? "\n" + thumbInfo : "\n（未设置特色图）") +
                        "\n\n编辑: " + cfg.site.TrimEnd('/') + "/wp-admin/post.php?post=" + postId + "&action=edit",
                        "✓ 已发布草稿", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    failCount++;
                    MessageBox.Show("发布失败：\n" + Path.GetFileName(mds[k]) + "\n\n" + err, "✗ 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            status.Text = "完成：成功 " + okCount + " 个，失败 " + failCount + " 个";
        };
    }

    public void LoadConfig()
    {
        cfg = ConfigStore.Load();
        if (cfg.user.Length == 0 || cfg.password.Length == 0)
        {
            using (var f = new ConfigForm(cfg))
            {
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                {
                    cfg = f.Result;
                    ConfigStore.Save(cfg);
                    status.Text = "已保存配置 → " + ConfigStore.ConfigPath;
                }
                else { status.Text = "未配置凭据，发布将失败"; }
            }
        }
    }
}

public static class Program
{
    static BlogConfig LoadCfg()
    {
        return ConfigStore.Load();
    }

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length > 0)
        {
            var cfg = LoadCfg();
            var mds = new List<string>();
            string img = null;
            bool quiet = false;
            foreach (var a in args)
            {
                string ext = Path.GetExtension(a).ToLowerInvariant();
                if (a.Equals("-quiet", StringComparison.OrdinalIgnoreCase)) quiet = true;
                else if (ext == ".md" || ext == ".markdown") mds.Add(a);
                else if ((ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".webp" || ext == ".bmp") && img == null) img = a;
            }
            var sb = new StringBuilder();
            for (int k = 0; k < mds.Count; k++)
            {
                string useImg = (k == 0) ? img : null;
                string title, postId, thumbInfo, err;
                err = Blogger.Publish(cfg, mds[k], useImg, out title, out postId, out thumbInfo);
                if (err.Length == 0)
                    sb.AppendLine("✓ " + Path.GetFileName(mds[k]) + " → ID " + postId + " (" + cfg.status + ")" +
                        (thumbInfo.Length > 0 ? "  [" + thumbInfo + "]" : ""));
                else
                    sb.AppendLine("✗ " + Path.GetFileName(mds[k]) + " → " + err);
            }
            if (quiet)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "publish-result.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            else
            {
                MessageBox.Show(sb.ToString(), "MD2Blog 发布结果", MessageBoxButtons.OK,
                    sb.ToString().Contains("✗") ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            return 0;
        }

        var form = new MainForm();
        form.LoadConfig();
        Application.Run(form);
        return 0;
    }
}
