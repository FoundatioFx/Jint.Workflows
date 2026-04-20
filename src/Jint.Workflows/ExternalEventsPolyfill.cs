namespace Jint.Workflows;

internal static class ExternalEventsPolyfill
{
    public const string Source = """
        (function() {
            globalThis.waitForEvent = async function(nameOrNames, opts) {
                var names = Array.isArray(nameOrNames) ? nameOrNames : [nameOrNames];
                var timeout = (opts && opts.timeout) || null;
                var result = await __wf_wait_event(names, timeout);

                if (result && result.__timeout) {
                    var err = new Error('waitForEvent timed out');
                    err.name = 'TimeoutError';
                    throw err;
                }

                if (Array.isArray(nameOrNames)) {
                    return result;
                }
                return result ? result.payload : null;
            };
        })();
        """;
}
