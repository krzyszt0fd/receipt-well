import { query } from "@anthropic-ai/claude-agent-sdk";
import { SYSTEM_PROMPT, REVIEW_JSON_SCHEMA, REVIEW_SCHEMA, Review } from "./review-schema.ts";

// Czytanie argumentów z stdin
async function readDiff(): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of process.stdin) chunks.push(chunk as Buffer);
  return Buffer.concat(chunks).toString("utf8");
}

// Proces review na podstawie git diffa
async function review(diff: string): Promise<Review> {

  // Konfiguracja agenta
  const result = query({
    prompt: `Zrecenzuj ten diff:\n\n${diff}`,
    options: {
      systemPrompt: SYSTEM_PROMPT,
      model: "claude-sonnet-5",
      tools: ["Read", "Grep", "Glob"],
      settingSources: [],
      permissionMode: "bypassPermissions",
      allowDangerouslySkipPermissions: true,
      maxBudgetUsd: 0.7,
      maxTurns: 20,
      outputFormat: { type: "json_schema", schema: REVIEW_JSON_SCHEMA },
    }
  });

  // Procesowanie odpowiedzi i ew. obsługa błędów
  for await (const message of result) {
    if (message.type !== "result") continue;
    if (message.subtype === "success") {
      console.log(`Total review cost: ${message.total_cost_usd} USD, number of turns: ${message.num_turns}.`);
      const parsed = REVIEW_SCHEMA.safeParse(message.structured_output);
      if (!parsed.success) throw new Error(`Niepoprawny structured output: ${parsed.error.message}`);
      return parsed.data;
    }
    throw new Error(`Review nie powiodło się (${message.subtype}): ${message.errors.join("; ")}`);
  }
  throw new Error("Agent nie zwrócił wyniku");
}

// Entry point całego procesu
const diff = await readDiff();
console.log(JSON.stringify(await review(diff), null, 2));