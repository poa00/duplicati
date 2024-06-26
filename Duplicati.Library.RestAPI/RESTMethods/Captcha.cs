// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using System;
using System.Linq;
using System.Collections.Generic;

namespace Duplicati.Server.WebServer.RESTMethods
{
    public class Captcha : IRESTMethodGET, IRESTMethodPOST
    {
        private class CaptchaEntry
        {
            public readonly string Answer;
            public readonly string Target;
            public int Attempts;
            public readonly DateTime Expires;

            public CaptchaEntry(string answer, string target)
            {
                Answer = answer;
                Target = target;
                Attempts = 4;
                Expires = DateTime.Now.AddMinutes(2);
            }
        }

        private static readonly object m_lock = new object();
        private static readonly Dictionary<string, CaptchaEntry> m_captchas = new Dictionary<string, CaptchaEntry>();

        public static bool SolvedCaptcha(string token, string target, string answer)
        {
            lock(m_lock)
            {
                CaptchaEntry tp;
                m_captchas.TryGetValue(token ?? string.Empty, out tp);
                if (tp == null)
                    return false;
                
                if (tp.Attempts > 0)
                    tp.Attempts--;
                
                return tp.Attempts >= 0 && string.Equals(tp.Answer, answer, StringComparison.OrdinalIgnoreCase) && tp.Target == target && tp.Expires >= DateTime.Now;
            }
        }

        public void GET(string key, RequestInfo info)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                info.ReportClientError("Missing token value", System.Net.HttpStatusCode.Unauthorized);
                return;
            }
            else
            {
                string answer = null;
                lock (m_lock)
                {
                    CaptchaEntry tp;
                    m_captchas.TryGetValue(key, out tp);
                    if (tp != null && tp.Expires > DateTime.Now)
                        answer = tp.Answer;
                }

                if (string.IsNullOrWhiteSpace(answer))
                {
                    info.ReportClientError("No such entry", System.Net.HttpStatusCode.NotFound);
                    return;
                }

                using (var bmp = CaptchaUtil.CreateCaptcha(answer))
                using (var ms = new System.IO.MemoryStream())
                {
                    info.Response.ContentType = "image/jpeg";
                    info.Response.ContentLength = ms.Length;
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                    ms.Position = 0;

                    info.Response.ContentType = "image/jpeg";
                    info.Response.ContentLength = ms.Length;
                    info.Response.SendHeaders();
                    ms.CopyTo(info.Response.Body);
                    info.Response.Send();
                }
            }        
        }

        public void POST(string key, RequestInfo info)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                var target = info.Request.Param["target"].Value;
                if (string.IsNullOrWhiteSpace(target))
                {
                    info.ReportClientError("Missing target parameter", System.Net.HttpStatusCode.BadRequest);
                    return;
                }

                var answer = CaptchaUtil.CreateRandomAnswer(minlength: 6, maxlength: 6);
                var nonce = Guid.NewGuid().ToString();

                string token;
                using (var ms = new System.IO.MemoryStream())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(answer + nonce);
                    ms.Write(bytes, 0, bytes.Length);
                    ms.Position = 0;
                    using(var hasher = Library.Utility.HashFactory.CreateHasher(Library.Utility.HashFactory.SHA256)){
                        token = Library.Utility.Utility.Base64PlainToBase64Url(Convert.ToBase64String(hasher.ComputeHash(ms)));
                    }
                }

                lock (m_lock)
                {
                    var expired = m_captchas.Where(x => x.Value.Expires < DateTime.Now).Select(x => x.Key).ToArray();
                    foreach (var x in expired)
                        m_captchas.Remove(x);

                    if (m_captchas.Count > 3)
                    {
                        info.ReportClientError("Too many captchas, wait 2 minutes and try again", System.Net.HttpStatusCode.ServiceUnavailable);
                        return;
                    }

                    m_captchas[token] = new CaptchaEntry(answer, target);
                }

                info.OutputOK(new
                {
                    token = token
                });
            }
            else
            {
                var answer = info.Request.Param["answer"].Value;
                var target = info.Request.Param["target"].Value;
                if (string.IsNullOrWhiteSpace(answer))
                {
                    info.ReportClientError("Missing answer parameter", System.Net.HttpStatusCode.BadRequest);
                    return;
                }
                if (string.IsNullOrWhiteSpace(target))
                {
                    info.ReportClientError("Missing target parameter", System.Net.HttpStatusCode.BadRequest);
                    return;
                }

                if (SolvedCaptcha(key, target, answer))
                    info.OutputOK();
                else
                    info.ReportClientError("Incorrect", System.Net.HttpStatusCode.Forbidden);
            }
        }
    }
}
