import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative } from "node:path";
import { it } from "vitest";
import { ProjectTrustStore } from "../src/core/trust-manager.ts";

it("captures null trust-store lookup and removal behavior", () => {
	const results = [];
	for (const scenario of JSON.parse(process.env.PISHARP_TRUST_SCENARIOS!)) {
		const root = mkdtempSync(join(tmpdir(), "pi-trust-null-probe-"));
		const parent = join(root, "parent");
		const project = join(parent, "project");
		const agent = join(root, "agent");
		const unrelated = join(root, "unrelated");
		mkdirSync(project, { recursive: true });
		mkdirSync(agent);
		try {
			const entries: Record<string, boolean | null> = { [project]: null, [unrelated]: null };
			if (Object.hasOwn(scenario, "parentDecision")) entries[parent] = scenario.parentDecision;
			writeFileSync(join(agent, "trust.json"), JSON.stringify(entries));
			const store = new ProjectTrustStore(agent);
			const before = store.getEntry(project);
			store.set(project, null);
			const after = store.getEntry(project);
			const saved = JSON.parse(readFileSync(join(agent, "trust.json"), "utf8"));
			const persisted = Object.fromEntries(Object.entries(saved)
				.map(([path, decision]) => [relative(root, path).replaceAll("\\", "/"), decision])
				.sort(([left], [right]) => String(left).localeCompare(String(right))));
			const formatEntry = (entry: { path: string; decision: boolean } | null) => entry === null
				? null
				: { path: relative(root, entry.path).replaceAll("\\", "/"), decision: entry.decision };
			results.push({ id: scenario.id, before: formatEntry(before), after: formatEntry(after), persisted });
		} finally {
			rmSync(root, { recursive: true, force: true });
		}
	}
	writeFileSync(process.env.PISHARP_TRUST_OUTPUT!, JSON.stringify(results));
});
