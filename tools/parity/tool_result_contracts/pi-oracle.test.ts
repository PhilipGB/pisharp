import { writeFileSync } from "node:fs";
import {
	type AssistantMessage,
	createAssistantMessageEventStream,
	type Model,
	type ToolResultMessage,
} from "@earendil-works/pi-ai";
import { Type } from "typebox";
import { it } from "vitest";
import { agentLoop } from "../src/agent-loop.ts";
import type { AgentEvent, AgentTool, AgentToolCall, AgentToolResult } from "../src/types.ts";

type Call = { id: string; name: string };

function usage(totalTokens = 0) {
	return {
		input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens,
		cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
	};
}

function model(): Model<"openai-responses"> {
	return {
		id: "mock", name: "mock", api: "openai-responses", provider: "mock",
		baseUrl: "https://example.invalid", reasoning: false, input: ["text"],
		cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
		contextWindow: 8192, maxTokens: 2048,
	};
}

function assistant(content: AssistantMessage["content"], reason: AssistantMessage["stopReason"] = "stop"): AssistantMessage {
	return { role: "assistant", content, api: "openai-responses", provider: "mock", model: "mock",
		usage: usage(), stopReason: reason, timestamp: Date.now() };
}

function tool(name: string, execute: AgentTool["execute"], parameters: unknown = Type.Object({}),
	outputSchema: unknown = Type.Object({ value: Type.Optional(Type.String()) })): AgentTool {
	return { name, label: name, description: name, parameters: parameters as never,
		outputSchema: outputSchema as never, execute };
}

type RunOptions = {
	turns: Call[][];
	tools: AgentTool[];
	toolExecution?: "parallel" | "sequential";
	beforeToolCall?: (context: unknown) => { block?: boolean; reason?: string; terminate?: boolean } | undefined;
	afterToolCall?: (args: unknown) => Record<string, unknown> | undefined;
};

async function run(options: RunOptions) {
	const executed: Record<string, number> = {};
	const order: string[] = [];
	const tools = options.tools.map((item) => ({ ...item, execute: async (...args: Parameters<AgentTool["execute"]>) => {
		executed[item.name] = (executed[item.name] ?? 0) + 1;
		order.push("start:" + args[0]);
		const result = await item.execute(...args);
		order.push("end:" + args[0]);
		return result;
	} }));
	let requests = 0;
	const stream = agentLoop([{ role: "user", content: "go", timestamp: Date.now() }],
		{ messages: [], tools }, { model: model(), toolExecution: options.toolExecution,
			convertToLlm: (messages) => messages.filter((message) =>
				["system", "user", "assistant", "toolResult"].includes(message.role)) as never,
			beforeToolCall: options.beforeToolCall ? async (context: unknown) => options.beforeToolCall!(context) : undefined,
			afterToolCall: options.afterToolCall ? async ({ result, isError }: { result: AgentToolResult; isError: boolean }) =>
				options.afterToolCall!({ result, isError }) : undefined },
		undefined, () => {
			const request = requests++;
			const stream = createAssistantMessageEventStream();
			queueMicrotask(() => {
				if (request < options.turns.length) {
					stream.push({ type: "done", reason: "toolUse", message: assistant(
						options.turns[request].map((call) => ({ type: "toolCall", id: call.id, name: call.name, arguments: {} } as const)),
						"toolUse") });
				} else {
					stream.push({ type: "done", reason: "stop", message: assistant([{ type: "text", text: "done" }]) });
				}
			});
			return stream;
		});
	const events: AgentEvent[] = [];
	for await (const event of stream) events.push(event);
	const messages = await stream.result();
	const finalized = new Map<string, AgentToolResult>();
	for (const event of events) {
		if (event.type === "tool_execution_end")
			finalized.set((event as { toolCallId: string }).toolCallId, (event as { result: AgentToolResult }).result);
	}
	const results = messages.filter((message): message is ToolResultMessage => message.role === "toolResult")
		.map((message) => {
			const result = finalized.get(message.toolCallId);
			return {
				callId: message.toolCallId,
				text: message.content.filter((part) => part.type === "text").map((part) => part.text).join("\n"),
				isError: message.isError === true,
				terminate: result?.terminate,
				structured: result?.structuredContent,
				details: result?.details,
				usage: message.usage?.totalTokens,
				images: message.content.filter((part) => part.type === "image").length,
			};
		});
	const updates = events.filter((event) => event.type === "tool_execution_update")
		.map((event) => (event as { partialResult: { content: { type: string; text?: string }[] } }).partialResult
			.content.filter((part) => part.type === "text").map((part) => part.text).join(""));
	return { requests, executed, results, order, updates };
}

const value = (text: string): { content: { type: "text"; text: string }[]; details: Record<string, unknown> } =>
	({ content: [{ type: "text", text }], details: {} });

it("writes the pinned tool-result contract oracle", async () => {
	const output: Record<string, unknown> = {};
	const permutations: [string, boolean[]][] = [
		["T01", [false]], ["T02", [true]], ["T03", [false, false]],
		["T04", [true, false]], ["T05", [false, true]], ["T06", [true, true]],
		["T07", [true, true, false]], ["T08", [true, true, true]], ["T09", [true, true]],
	];
	for (const [id, flags] of permutations) {
		const tools = flags.map((terminate, index) => tool("tool" + index, async () => ({
			...value("result " + index), terminate, ...(id === "T09" && index === 1 ? { isError: true } : {}),
		})));
		output[id] = await run({ tools, turns: [flags.map((_, index) => ({ id: "call-" + index, name: "tool" + index }))] });
	}

	output.T10 = await run({
		tools: [0, 1].map((index) => tool("tool" + index, async () => {
			await new Promise((resolve) => setTimeout(resolve, index === 1 ? 0 : 20));
			return { ...value("result " + index), terminate: true };
		})),
		turns: [[{ id: "call-0", name: "tool0" }, { id: "call-1", name: "tool1" }]],
	});
	output.T11 = await run({
		toolExecution: "sequential",
		tools: [tool("tool0", async () => ({ ...value("first"), terminate: true })),
			tool("tool1", async () => value("second"))],
		turns: [[{ id: "call-0", name: "tool0" }, { id: "call-1", name: "tool1" }]],
	});
	output.T12 = await run({
		tools: [tool("tool0", async () => ({ ...value("result 0"), terminate: true })),
			tool("tool1", async () => value("result 1"))],
		turns: [[{ id: "call-0", name: "tool0" }, { id: "call-1", name: "tool1" }]],
	});

	output.E04 = await run({
		tools: [
			tool("invalid", async () => value("ran"), Type.Object({ value: Type.Integer() })),
			tool("blocked", async () => value("ran")),
		],
		turns: [[{ id: "call-unknown", name: "unknown" },
			{ id: "call-invalid", name: "invalid" }, { id: "call-blocked", name: "blocked" }]],
		beforeToolCall: (context) => (context as { toolCall: { name: string } }).toolCall.name === "blocked"
			? { block: true, reason: "policy blocked" } : undefined,
	});
	output.E05 = await run({
		tools: [tool("tool0", async () => ({ ...value("invalid"), structuredContent: { count: "wrong" } }),
			Type.Object({}), Type.Object({ count: Type.Integer() }))],
		turns: [[{ id: "call-0", name: "tool0" }]],
	});
	output.E02 = await run({ tools: [tool("tool0", async () => ({
		...value("bad"), isError: true, details: { source: "domain" }, structuredContent: { count: 2 } }))],
		turns: [[{ id: "call-0", name: "tool0" }]] });
	output.E03 = await run({ tools: [tool("tool0", async () => { throw new Error("boom"); })],
		turns: [[{ id: "call-0", name: "tool0" }]] });
	output.E06 = await run({ tools: [
		tool("tool0", async () => ({ ...value("bad"), isError: true })),
		tool("tool1", async () => value("good"))],
		turns: [[{ id: "call-0", name: "tool0" }, { id: "call-1", name: "tool1" }]] });
	output.E07 = {
		single: await run({ tools: [tool("tool0", async () => ({
			...value("bad"), isError: true, terminate: true }))],
			turns: [[{ id: "call-0", name: "tool0" }]] }),
		mixed: await run({ tools: [
			tool("tool0", async () => ({ ...value("bad"), isError: true, terminate: true })),
			tool("tool1", async () => value("good"))],
			turns: [[{ id: "call-0", name: "tool0" }, { id: "call-1", name: "tool1" }]] }),
	};

	output.H01 = await run({ tools: [tool("tool0", async () => value("ok"))],
		turns: [[{ id: "call-0", name: "tool0" }]], afterToolCall: () => ({ terminate: true }) });
	output.H02 = await run({ tools: [tool("tool0", async () => ({ ...value("ok"), terminate: true }))],
		turns: [[{ id: "call-0", name: "tool0" }]], afterToolCall: () => ({ terminate: false }) });
	output.H03 = await run({ tools: [tool("tool0", async () => ({
		...value("original"), details: { ui: 1 }, usage: usage(7) }))],
		turns: [[{ id: "call-0", name: "tool0" }]],
		afterToolCall: () => ({ content: [{ type: "text", text: "hooked" }], isError: true }) });
	output.H04 = await run({ tools: [tool("tool0", async () => ({
		...value("original"), details: { ui: 1 }, structuredContent: { count: 1 }, usage: usage(7) }))],
		turns: [[{ id: "call-0", name: "tool0" }]],
		afterToolCall: () => ({ content: [{ type: "text", text: "redacted" }] }) });
	output.H05 = await run({ tools: [tool("tool0", async () => value("original"))],
		turns: [[{ id: "call-0", name: "tool0" }]],
		afterToolCall: () => { throw new Error("hook boom"); } });
	output.H06 = await run({ tools: [tool("tool0", async () => ({
		...value("original"), details: { ui: 1 }, structuredContent: { count: 1 }, usage: usage(7) }))],
		turns: [[{ id: "call-0", name: "tool0" }]],
		afterToolCall: () => ({ content: [{ type: "text", text: "hooked" }], details: { ui: 2 } }) });
	output.H07 = await run({ tools: [tool("tool0", async () => ({
		...value("original"), details: { ui: 1 }, structuredContent: { count: 1 }, usage: usage(7) }))],
		turns: [[{ id: "call-0", name: "tool0" }]],
		afterToolCall: () => ({ content: [{ type: "text", text: "hooked" }] }) });

	output.R01 = await run({ tools: [tool("tool0", async () => ({
		...value("model text"), details: { ui: "details" }, structuredContent: { count: 2 }, usage: usage(11) }))],
		turns: [[{ id: "call-0", name: "tool0" }]] });
	output.R02 = await run({ tools: [tool("tool0", async () => ({
		...value("valid"), structuredContent: { count: 2 } }))],
		turns: [[{ id: "call-0", name: "tool0" }]] });
	output.R03 = await run({ tools: [tool("tool0", async () => ({
		content: [{ type: "text", text: "with image" }, { type: "image", data:
			"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==",
			mimeType: "image/png" }], details: {} }))],
		turns: [[{ id: "call-0", name: "tool0" }]] });
	output.R04 = await run({ tools: [tool("tool0", async (_id, _params, _signal, onUpdate) => {
		onUpdate?.({ content: [{ type: "text", text: "first" }], details: {} });
		onUpdate?.({ content: [{ type: "text", text: "second" }], details: {} });
		return { ...value("finished"), usage: usage(13) };
	})], turns: [[{ id: "call-0", name: "tool0" }]] });

	output.P01 = await run({
		tools: [0, 1, 2].map((index) => tool("tool" + index, async () => {
			await new Promise((resolve) => setTimeout(resolve, (2 - index) * 15));
			return value("result " + index);
		})),
		turns: [[{ id: "call-0", name: "tool0" }, { id: "call-1", name: "tool1" }, { id: "call-2", name: "tool2" }]],
	});

	const target = process.env.PISHARP_TOOL_RESULT_PI_OUTPUT;
	if (!target) throw new Error("PISHARP_TOOL_RESULT_PI_OUTPUT is required");
	writeFileSync(target, JSON.stringify(output, null, 2) + "\n");
}, 120000);
