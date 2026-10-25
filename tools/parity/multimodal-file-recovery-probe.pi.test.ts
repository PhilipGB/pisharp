import { writeFileSync } from "node:fs";
import { fauxAssistantMessage } from "@earendil-works/pi-ai/compat";
import type { Args } from "../src/cli/args.ts";
import { buildInitialMessage } from "../src/cli/initial-message.ts";
import { processFileArguments } from "../src/cli/file-processor.ts";
import type { TranscriptContext } from "../../ai/src/types.ts";
import { afterEach, expect, test } from "vitest";
import { createHarness, type Harness } from "./suite/harness.ts";

const harnesses: Harness[] = [];

afterEach(() => {
	while (harnesses.length) harnesses.pop()?.cleanup();
});

function summarizeLastUser(context: TranscriptContext) {
	const user = context.messages.findLast((message) => message.role === "user");
	if (!user || user.role !== "user") throw new Error("Provider context omitted the user message");
	const parts = typeof user.content === "string" ? [{ type: "text", text: user.content }] : user.content;
	return parts.map((part) =>
		part.type === "image"
			? { type: part.type, mimeType: part.mimeType, data: part.data }
			: { type: part.type, text: part.type === "text" ? part.text : "" },
	);
}

test("positional truncated image is omitted while the following image reaches provider", async () => {
	const root = process.env.PISHARP_FILE_RECOVERY_ROOT!;
	process.chdir(root);
	const scenarios = JSON.parse(process.env.PISHARP_FILE_RECOVERY_SCENARIOS!) as Array<{
		id: string;
		prompt: string;
		files: string[];
	}>;
	const capture = async () => {
		const results = [];
		for (const scenario of scenarios) {
			const processed = await processFileArguments(scenario.files, { autoResizeImages: false });
			const parsed = { messages: [scenario.prompt], fileArgs: [], unknownFlags: new Map(), diagnostics: [] } as Args;
			const initial = buildInitialMessage({ parsed, fileText: processed.text, fileImages: processed.images });
			const harness = await createHarness();
			harnesses.push(harness);
			let providerParts: ReturnType<typeof summarizeLastUser> = [];
			harness.setResponses([
				(context) => {
					providerParts = summarizeLastUser(context);
					return fauxAssistantMessage("continued");
				},
			]);
			await harness.session.prompt(initial.initialMessage ?? "", {
				images: (initial.initialImages ?? []).map((image) => ({
					type: "image" as const,
					data: image.data,
					mimeType: image.mimeType,
				})),
			});
			harness.cleanup();
			harnesses.pop();
			results.push({ id: scenario.id, providerParts });
		}
		return results;
	};
	const first = await capture();
	const second = await capture();
	writeFileSync(process.env.PISHARP_FILE_RECOVERY_OUTPUT!, JSON.stringify({ first, second }));
	expect(first).toEqual(second);
});
