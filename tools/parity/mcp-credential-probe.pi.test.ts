import { writeFileSync } from "node:fs";
import { it } from "vitest";
import { InMemoryAuthStorageBackend } from "../src/core/auth-storage.ts";
import { McpOAuthCredentialStore } from "../src/extensions/mcp/oauth.ts";

it("records credential operations for the PiSharp differential", async () => {
	const results = [];
	for (const scenario of JSON.parse(process.env.PISHARP_CREDENTIAL_SCENARIOS!)) {
		const backend = new InMemoryAuthStorageBackend();
		const store = new McpOAuthCredentialStore(backend);
		const steps = [];
		for (const operation of scenario.operations) {
			const { action, name, url, account } = operation;
			const state = { serverUrl: url, tokens: { access_token: account, refresh_token: `${account}-refresh`, token_type: "Bearer" } };
			let result;
			if (action === "seed") backend.withLock((current) => ({ result: undefined, next: JSON.stringify({ ...JSON.parse(current ?? "{}"), [new URL(url).href]: state }) }));
			else if (action === "save") await store.forServer(name, url).save(state);
			else if (action === "remove") result = store.remove(name, url);
			else {
				const tokens = action === "peek" ? store.tokens(name, url) : (await store.forServer(name, url).load())?.tokens;
				result = tokens ? { access: tokens.access_token, refresh: tokens.refresh_token } : null;
			}
			const keys = Object.keys(JSON.parse(backend.withLock((current) => ({ result: current ?? "{}" })))).sort();
			steps.push({ result: result ?? null, keys });
		}
		results.push({ id: scenario.id, steps });
	}
	writeFileSync(process.env.PISHARP_CREDENTIAL_OUTPUT!, JSON.stringify(results));
});
