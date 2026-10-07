const origin = 'https://huggingface.co';
const fixtureBase = process.env.PI_PARITY_HF_BASE_URL;

if (fixtureBase) {
  const target = new URL(fixtureBase);
  const install = () => {
    // Pi installs its Undici fetch during CLI setup, after Node preloads. Wrap
    // that configured fetch once setup has finished so the fixture stays local.
    if (process.env.PI_CODING_AGENT !== 'true') {
      setImmediate(install);
      return;
    }
    const configuredFetch = globalThis.fetch.bind(globalThis);
    globalThis.fetch = (input, init) => {
      const url = input instanceof URL ? input.href : typeof input === 'string' ? input : input.url;
      if (!url.startsWith(`${origin}/`)) return configuredFetch(input, init);
      const rewritten = new URL(url);
      rewritten.protocol = target.protocol;
      rewritten.host = target.host;
      return configuredFetch(rewritten, init);
    };
  };
  setImmediate(install);
}
