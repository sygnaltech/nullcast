# Havoc MCP Not Detected - Troubleshooting Notes

## Problem
Havoc MCP server is defined in project `.mcp.json` but Claude Code does not detect it.
User has restarted 10+ times. No approval prompt ever appears.

## Environment
- Claude Code running inside VS 2022 (via dliedke.ClaudeCodeExtension)
- That extension is a CLI wrapper - it just launches Claude Code CLI
- So Claude Code CLI config rules should apply

## What Works
- Global MCPs in `C:\Users\OEM\.claude.json` → clipboard, playwright, posthog all show up
- claude.ai MCPs → Figma, Procura, Webflow all show up

## What Doesn't Work
- Project `.mcp.json` at `D:\Projects\Apps\VideoPlayer\.mcp.json` → havoc NOT detected
- File exists, valid JSON, correct format
- Never prompted to approve project-level MCP servers

## Config Details

### `.mcp.json` (project root) - EXISTS, NOT DETECTED
```json
{
  "mcpServers": {
    "havoc": {
      "type": "http",
      "url": "https://spec-mcp.sygnal.com/mcp",
      "headers": {
        "X-MCP-API-Key": "mcp_DQeoVHWCADRN916dVjFzmor8NEux0xaQ"
      }
    }
  }
}
```

### `~/.claude.json` global - project entry has empty arrays
```json
"D:/Projects/Apps/VideoPlayer": {
  "enabledMcpjsonServers": [],
  "disabledMcpjsonServers": [],
  ...
}
```

## Constraints
- CANNOT add havoc to global `~/.claude.json` mcpServers - API key is project-specific
- MUST remain project-scoped only

## Theories
1. `enabledMcpjsonServers: []` in global config might be actively blocking detection
2. VS 2022 extension may launch CLI with a different working directory
3. The approval flow for project .mcp.json may be broken or not triggering

## NOT YET TRIED
- Adding "havoc" to `enabledMcpjsonServers` array in global config's project entry
- Checking what working directory the VS 2022 extension actually passes to Claude Code CLI
- Running `claude mcp list` from CLI directly in project directory
- Checking if `.mcp.json` works in other projects
