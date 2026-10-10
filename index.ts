import "dotenv/config";
import OpenAI from "openai";
import { stdout, stdin } from "node:process";
import { createInterface } from "node:readline/promises";

const rl = createInterface({
  input: stdin,
  output: stdout,
});

if (process.env.OPENAI_API_KEY === undefined) {
  throw new Error(
    "OPENAI_API_KEY is not defined in the environment variables.",
  );
}
const openai = new OpenAI({
  apiKey: process.env.OPENAI_API_KEY,
});
let id = 0;

interface Task {
  id: number;
  name: string;
  completed: boolean;
}

type AgentReply =
  | { type: "tool_call"; toolName: string; parameters: Record<string, unknown> }
  | { type: "final"; message: string };

type Message = { role: "system" | "user" | "assistant"; content: string };

const MAX_STEPS = 10;

const tasks: Task[] = [];

function listTasks(): Task[] {
  return [...tasks];
}

function createTask(name: string): Task {
  const newTask: Task = {
    id: ++id,
    name,
    completed: false,
  };
  tasks.push(newTask);
  return newTask;
}

function deleteTask(taskId: number): boolean {
  const index = tasks.findIndex((task) => task.id === taskId);
  if (index !== -1) {
    tasks.splice(index, 1);
    return true;
  }
  return false;
}

function completeTask(taskId: number): boolean {
  const task = tasks.find((task) => task.id === taskId);
  if (task) {
    task.completed = true;
    return true;
  }
  return false;
}

const toolList = [
  {
    toolName: "list_tasks",
    description: "Use this tool when you need to list all existing tasks",
    parameters: {},
  },
  {
    toolName: "create_task",
    description: "Use this tool when you need to create a new task",
    parameters: {
      taskName: "string",
    },
  },
  {
    toolName: "delete_task",
    description: "Use this tool when you need to delete an existing task",
    parameters: {
      taskId: "number",
    },
  },
  {
    toolName: "complete_task",
    description:
      "Use this tool when you need to mark an existing task as complete",
    parameters: {
      taskId: "number",
    },
  },
];

const messages: Message[] = [
  {
    role: "system",
    content: `You are a project manager assistant that manages the user's tasks.
Respond with ONLY one JSON object, in exactly one of these two shapes:

1. To use a tool:
{ "type": "tool_call", "toolName": "<tool name>", "parameters": { ... } }

2. To answer the user (when no more tools are needed):
{ "type": "final", "message": "<your answer>" }

Request one tool at a time. You will receive its result in the next message.
Never invent task IDs; list tasks first if you need them.
Available tools: ${JSON.stringify(toolList)}`,
  },
];

function runTool(toolName: string, parameters: Record<string, unknown>): unknown {
  switch (toolName) {
    case "list_tasks":
      return listTasks();
    case "create_task":
      if (typeof parameters.taskName !== "string" || !parameters.taskName.trim()) {
        return { error: "taskName must be a non-empty string" };
      }
      return createTask(parameters.taskName);
    case "delete_task":
    case "complete_task": {
      const taskId = parameters.taskId;
      if (typeof taskId !== "number" || !Number.isInteger(taskId)) {
        return { error: "taskId must be an integer" };
      }
      return toolName === "delete_task" ? deleteTask(taskId) : completeTask(taskId);
    }
    default:
      return { error: `Unknown tool: ${toolName}` };
  }
}

function parseReply(raw: string): AgentReply | string {
  let data: any;
  try {
    data = JSON.parse(raw);
  } catch {
    return "Your reply was not valid JSON. Reply with one JSON object only.";
  }
  if (data?.type === "final" && typeof data.message === "string") {
    return data;
  }
  if (
    data?.type === "tool_call" &&
    typeof data.toolName === "string" &&
    typeof data.parameters === "object" &&
    data.parameters !== null
  ) {
    return data;
  }
  return 'Invalid reply shape. Use { "type": "tool_call", ... } or { "type": "final", ... }.';
}

while (true) {
  const userInput = await rl.question("> ");
  if (userInput.trim().toLowerCase() === "exit") {
    break;
  }
  messages.push({ role: "user", content: userInput });

  let answered = false;
  for (let step = 0; step < MAX_STEPS; step++) {
    const result = await openai.chat.completions.create({
      model: "gpt-6-luna",
      messages,
    });

    const raw = result.choices[0]?.message?.content ?? "";
    messages.push({ role: "assistant", content: raw });

    const reply = parseReply(raw);
    if (typeof reply === "string") {
      messages.push({ role: "user", content: reply });
      continue;
    }

    if (reply.type === "final") {
      console.log(reply.message);
      answered = true;
      break;
    }

    const toolResult = runTool(reply.toolName, reply.parameters);
    messages.push({
      role: "user",
      content: `Tool result for ${reply.toolName}: ${JSON.stringify(toolResult)}`,
    });
  }

  if (!answered) {
    console.log("The agent could not finish within the step limit.");
  }
}

rl.close();
