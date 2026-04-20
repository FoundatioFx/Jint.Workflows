namespace Jint.Workflows.Fetch;

internal static class FetchPolyfill
{
    public const string Source = """
        (function() {
            function Response(data) {
                this.ok = !!data.ok;
                this.status = data.status;
                this.statusText = data.statusText || '';
                this.url = data.url || '';
                this.redirected = !!data.redirected;
                this.headers = data.headers || {};
                this._body = data.bodyText == null ? '' : data.bodyText;
                this.bodyUsed = false;
            }
            Response.prototype.text = function() {
                if (this.bodyUsed) return Promise.reject(new TypeError('Body already used'));
                this.bodyUsed = true;
                return Promise.resolve(this._body);
            };
            Response.prototype.json = function() {
                if (this.bodyUsed) return Promise.reject(new TypeError('Body already used'));
                this.bodyUsed = true;
                try { return Promise.resolve(JSON.parse(this._body)); }
                catch (e) { return Promise.reject(e); }
            };
            Response.prototype.clone = function() {
                return new Response({
                    ok: this.ok,
                    status: this.status,
                    statusText: this.statusText,
                    url: this.url,
                    redirected: this.redirected,
                    headers: Object.assign({}, this.headers),
                    bodyText: this._body
                });
            };

            globalThis.Response = Response;
            globalThis.fetch = async function(input, init) {
                var data = await __wf_fetch_internal(input, init || null);
                return new Response(data);
            };
        })();
        """;
}
