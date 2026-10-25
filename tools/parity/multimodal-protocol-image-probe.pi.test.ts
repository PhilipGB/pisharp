import { readFileSync, writeFileSync } from "node:fs";
import { afterEach, expect, test, vi } from "vitest";
import type { AgentSessionRuntime } from "../src/core/agent-session-runtime.ts";
import { buildInitialMessage } from "../src/cli/initial-message.ts";
import { processFileArguments } from "../src/cli/file-processor.ts";
import type { Args } from "../src/cli/args.ts";
import { runPrintMode } from "../src/modes/print-mode.ts";
import { runRpcMode } from "../src/modes/rpc/rpc-mode.ts";
import { fauxAssistantMessage } from "@earendil-works/pi-ai/compat";
import { createHarness, type Harness } from "./suite/harness.ts";

const io = vi.hoisted(() => ({
	lineHandler: undefined as ((line: string) => void) | undefined,
	output: [] as string[],
}));

vi.mock("../src/core/output-guard.ts", () => ({
	flushRawStdout: vi.fn(async () => {}),
	takeOverStdout: vi.fn(),
	waitForRawStdoutBackpressure: vi.fn(async () => {}),
	writeRawStdout: (line: string) => io.output.push(line),
}));

vi.mock("../src/modes/rpc/jsonl.ts", () => ({
	attachJsonlLineReader: vi.fn((_stream: NodeJS.ReadableStream, onLine: (line: string) => void) => {
		io.lineHandler = onLine;
		return () => {};
	}),
	serializeJsonLine: (value: unknown) => `${JSON.stringify(value)}\n`,
}));

function imageParts(harness: Harness) {
	const user = harness.session.messages.findLast((message) => message.role === "user");
	if (!user || !Array.isArray(user.content))
		throw new Error(
			`Pinned Pi produced no image user message: ${JSON.stringify({ messages: harness.session.messages, output: io.output })}`,
		);
	return user.content.map((part) =>
		part.type === "text"
			? { type: "text", text: part.text }
			: { type: "image", mimeType: part.mimeType, data: part.data },
	);
}

function runtimeHost(harness: Harness): AgentSessionRuntime {
	return {
		session: harness.session,
		setRebindSession: () => {},
		dispose: async () => {},
		newSession: async () => ({ cancelled: true }),
		switchSession: async () => ({ cancelled: true }),
		fork: async () => ({ cancelled: true, selectedText: "" }),
	} as unknown as AgentSessionRuntime;
}

afterEach(() => {
	io.lineHandler = undefined;
	io.output = [];
});

test("captures pinned JSON and RPC image prompts", async () => {
	const directory = process.env.PISHARP_PROTOCOL_INPUT_DIR!;
	const brokenPath = `${directory}/broken.png`;
	const filePath = `${directory}/pixel.png`;
	const bytes = readFileSync(filePath);
	const image = { type: "image" as const, mimeType: "image/png", data: bytes.toString("base64") };
	const output: Record<string, unknown> = {};

	const jsonHarness = await createHarness();
	try {
		jsonHarness.setResponses([fauxAssistantMessage("done")]);
		const files = await processFileArguments([brokenPath, filePath], { autoResizeImages: false });
		const initial = buildInitialMessage({
			parsed: { messages: ["Describe the fixture"] } as Args,
			fileText: files.text,
			fileImages: files.images,
		});
		await runPrintMode(runtimeHost(jsonHarness), {
			mode: "json",
			initialMessage: initial.initialMessage,
			initialImages: initial.initialImages,
		});
		output.json = imageParts(jsonHarness);
	} finally {
		jsonHarness.cleanup();
	}

	const rpcHarness = await createHarness();
	try {
		rpcHarness.setResponses([fauxAssistantMessage("done")]);
		io.output = [];
		void runRpcMode(runtimeHost(rpcHarness));
		await vi.waitFor(() => expect(io.lineHandler).toBeDefined());
		io.lineHandler!(JSON.stringify({ id: "image", type: "prompt", message: "Describe the fixture", images: [image] }));
		await vi.waitFor(() => expect(io.output.join("")).toContain("agent_settled"));
		output.rpc = imageParts(rpcHarness);
	} finally {
		await rpcHarness.session.abort();
		rpcHarness.cleanup();
	}

	expect(output.json).toEqual(expect.arrayContaining([expect.objectContaining({ type: "image", mimeType: "image/png" })]));
	expect(output.rpc).toEqual(expect.arrayContaining([expect.objectContaining({ type: "image", mimeType: "image/png" })]));
	writeFileSync(process.env.PISHARP_PROTOCOL_OUTPUT!, JSON.stringify(output));
});
