using System.Collections.Frozen;

namespace DotnetDebugMcp;

/// <summary>
/// Ops manuals for MCP tools (pull via <c>man</c>). Not shell man(1), not ADR.
/// Runtime how/why + NEVER — durable design rationale stays in ADRs/KB.
/// </summary>
internal static class ManPages
{
    internal const string Toc =
        """
        NAME
          man — MCP ops manual for DotnetDebugMcp (not shell).

        SYNOPSIS
          man
          man tool=<tool_name>

        DESCRIPTION
          Operating procedure for debug tools. ListTools = capabilities only.
          Call man on first contact, stuck session, or before rebuild while debugging.

        PAGES
          debug_launch
          debug_attach
          debug_stop
          debug_stop_context
          debug_continue
          debug_set_breakpoints

        RESOURCES
          debug://state
          debug://breakpoints
          debug://threads

        SEE ALSO
          Server instructions on initialize; host rebuild only AFTER debug_stop.
        """;

    private static readonly FrozenDictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["debug_launch"] =
            """
            NAME
              debug_launch — start a .NET debug session (netcoredbg / DAP).

            SYNOPSIS
              debug_set_breakpoints workspace_path=… target_path=… breakpoints=[…]
              debug_launch workspace_path=… target_path=… [netcoredbg_path] [program_args]
              → wait stopped → debug_stop_context (or stack/variables) → debug_continue | debug_stop

            SESSION GRAPH (usual)
              1) set breakpoints (JSON under workspace; key = target_path)
              2) debug_launch (loads those BPs for the same target_path)
              3) on stopped: prefer debug_stop_context; else stack / variables / step_*
              4) debug_continue to run again, OR debug_stop to end session

            WHY STOP BEFORE REBUILD
              netcoredbg keeps the target PDB/exe open. Rebuild while session alive →
              "file locked by netcoredbg.exe". Always debug_stop first, then rebuild, then new launch.

            NEVER
              taskkill netcoredbg / kill the debug process from outside — MCP session may go Error;
              if the app is wedged, prefer debug_stop
              rebuild / publish the debugged dll|exe while launch session is active
              call step_* / variables / stack while not stopped

            SEE ALSO
              man tool=debug_stop_context
              man tool=debug_stop
              man tool=debug_continue
              man tool=debug_attach
              man tool=debug_set_breakpoints
            """,

        ["debug_attach"] =
            """
            NAME
              debug_attach — attach DAP to an already-running .NET process.

            SYNOPSIS
              debug_attach workspace_path=… process_id=… [target_path] [netcoredbg_path]

            WHEN
              Process already running; you need breakpoints/stack without relaunching.

            NOTES
              Optional target_path loads saved breakpoints for that target key.
              Same session rules as launch: stop → debug_stop_context → continue | debug_stop.
              Rebuild of target binaries still requires debug_stop first (PDB lock).

            SEE ALSO
              man tool=debug_launch
              man tool=debug_stop_context
              man tool=debug_stop
            """,

        ["debug_stop"] =
            """
            NAME
              debug_stop — end the current debug session cleanly.

            SYNOPSIS
              debug_stop

            WHAT IT DOES
              Sends a polite wind-down (including continue so the process is not left hung),
              disposes the DAP client, releases file locks on PDB/exe.

            CALL BEFORE
              rebuild / publish / aid-publish of the debugged target
              starting a new debug_launch for the same target when a session is still open

            NEVER
              taskkill netcoredbg instead of debug_stop when the MCP session should stay healthy

            AFTER
              Need a new debug_launch (or attach) to debug again.

            SEE ALSO
              man tool=debug_launch
              man tool=debug_continue
            """,

        ["debug_stop_context"] =
            """
            NAME
              debug_stop_context — stack + variables in one call after stopped.

            SYNOPSIS
              debug_stop_context [frame_index] [fast] [max_depth] [max_children_per_node] [time_budget_ms] [format]

            WHEN
              Target is stopped (breakpoint / exception). Prefer this over separate
              debug_stack_trace + debug_variables to cut round-trips.

            NOTES
              Variable args match debug_variables. Resources debug://state|breakpoints|threads
              give lighter snapshots without a tool call.

            SEE ALSO
              man tool=debug_launch
              debug_stack_trace / debug_variables
            """,

        ["debug_continue"] =
            """
            NAME
              debug_continue — resume after breakpoint stop.

            SYNOPSIS
              debug_continue

            WHEN
              Target is stopped on a breakpoint (or equivalent DAP stopped) and you want it to run again.

            NOT THE SAME AS
              debug_stop — continue keeps the session; stop ends it and unlocks PDB.

            SEE ALSO
              man tool=debug_launch
              man tool=debug_stop
              debug_step_over / debug_step_into / debug_step_out (only while stopped)
            """,

        ["debug_set_breakpoints"] =
            """
            NAME
              debug_set_breakpoints — persist breakpoints for a target before launch/attach.

            SYNOPSIS
              debug_set_breakpoints workspace_path=… target_path=… breakpoints=[{file_path,line,…}]

            HOW IT WORKS
              Writes .dotnet-debug-mcp-breakpoints.json under workspace_path.
              target_path is the key (csproj|dll|exe) used later by debug_launch / attach.

            TYPICAL ORDER
              set_breakpoints → debug_launch (same workspace_path + target_path)

            SEE ALSO
              debug_list_breakpoints
              debug_clear_breakpoints
              man tool=debug_launch
            """,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyCollection<string> KnownTools => Pages.Keys;

    internal static string Resolve(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
            return Toc.TrimEnd() + "\n";

        var key = tool.Trim();
        if (Pages.TryGetValue(key, out var page))
            return page.TrimEnd() + "\n";

        var known = string.Join(", ", Pages.Keys.Order(StringComparer.Ordinal));
        return $"Unknown man page: {key}\nKnown: {known}\nCall man with no tool for the table of contents.\n";
    }
}
