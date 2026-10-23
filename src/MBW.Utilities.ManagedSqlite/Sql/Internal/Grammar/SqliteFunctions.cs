using System;
using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The SQL functions SQLite builds in, as far as resolving a generated column or a PRIMARY KEY or UNIQUE term looks at
/// them: how many arguments each takes, and whether it is deterministic, an aggregate or a window function. These are
/// the functions of a default build: func.c, window.c, date.c and json.c, with the math functions. Functions that
/// extensions register (FTS, R*Tree) and SQLite's internal functions are not here; a schema may name them, as SQLite
/// tolerates an unknown function while it loads the schema.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/func.c#L3266 (sqlite3RegisterBuiltinFunctions)
/// </summary>
internal static class SqliteFunctions
{
    [Flags]
    public enum FunctionFlags
    {
        None = 0,

        /// <summary>
        /// SQLITE_FUNC_CONSTANT: deterministic
        /// </summary>
        Constant = 1,

        /// <summary>
        /// SQLITE_FUNC_SLOCHNG: constant within one statement, like the date and time functions
        /// </summary>
        SlowChange = 2,

        /// <summary>
        /// An aggregate (xFinalize is set)
        /// </summary>
        Aggregate = 4,

        /// <summary>
        /// Usable as a window function (xValue is set)
        /// </summary>
        WindowValue = 8,

        /// <summary>
        /// SQLITE_FUNC_WINDOW: a built-in window function, usable only with OVER
        /// </summary>
        Window = 16,

        /// <summary>
        /// SQLITE_DIRECTONLY: not usable from the schema
        /// </summary>
        DirectOnly = 32,

        /// <summary>
        /// SQLITE_FUNC_UNLIKELY: likely(), unlikely() and likelihood()
        /// </summary>
        Unlikely = 64
    }

    public readonly struct Function
    {
        public readonly string Name;

        /// <summary>
        /// The number of arguments: -1 for any, -3 for at least one, -4 for at least two (FuncDef.nArg)
        /// </summary>
        public readonly int ArgCount;

        public readonly FunctionFlags Flags;

        public Function(string name, int argCount, FunctionFlags flags)
        {
            Name = name;
            ArgCount = argCount;
            Flags = flags;
        }
    }

    private const FunctionFlags C = FunctionFlags.Constant;
    private const FunctionFlags V = FunctionFlags.None;
    private const FunctionFlags D = FunctionFlags.SlowChange;
    private const FunctionFlags PureDate = FunctionFlags.Constant | FunctionFlags.SlowChange;
    private const FunctionFlags Agg = FunctionFlags.Aggregate | FunctionFlags.WindowValue;
    private const FunctionFlags Win = FunctionFlags.Aggregate | FunctionFlags.WindowValue | FunctionFlags.Window;

    private static readonly Function[] All =
    {
        // func.c: FUNCTION is deterministic (C), VFUNCTION is not (V), DFUNCTION changes slowly (D), SFUNCTION is
        // direct-only, INLINE_FUNC and MFUNCTION are deterministic, WAGGREGATE is an aggregate with a window value
        new("load_extension", 1, FunctionFlags.DirectOnly), new("load_extension", 2, FunctionFlags.DirectOnly),
        new("sqlite_compileoption_used", 1, D), new("sqlite_compileoption_get", 1, D),
        new("unlikely", 1, C | FunctionFlags.Unlikely), new("likelihood", 2, C | FunctionFlags.Unlikely),
        new("likely", 1, C | FunctionFlags.Unlikely),
        new("ltrim", 1, C), new("ltrim", 2, C), new("rtrim", 1, C), new("rtrim", 2, C), new("trim", 1, C),
        new("trim", 2, C),
        new("min", -3, C), new("min", 1, Agg), new("max", -3, C), new("max", 1, Agg),
        new("typeof", 1, C), new("subtype", 1, C), new("length", 1, C), new("octet_length", 1, C),
        new("instr", 2, C), new("printf", -1, C), new("format", -1, C), new("unicode", 1, C), new("char", -1, C),
        new("abs", 1, C), new("round", 1, C), new("round", 2, C), new("upper", 1, C), new("lower", 1, C),
        new("hex", 1, C), new("unhex", 1, C), new("unhex", 2, C), new("concat", -3, C), new("concat_ws", -4, C),
        new("ifnull", 2, C), new("random", 0, V), new("randomblob", 1, V), new("nullif", 2, C),
        new("sqlite_version", 0, D), new("sqlite_source_id", 0, D), new("sqlite_log", 2, C), new("unistr", 1, C),
        new("quote", 1, C), new("unistr_quote", 1, C), new("last_insert_rowid", 0, V), new("changes", 0, V),
        new("total_changes", 0, V), new("replace", 3, C), new("zeroblob", 1, C), new("substr", 2, C),
        new("substr", 3, C), new("substring", 2, C), new("substring", 3, C),
        new("sum", 1, Agg), new("total", 1, Agg), new("avg", 1, Agg), new("count", 0, Agg), new("count", 1, Agg),
        new("group_concat", 1, Agg), new("group_concat", 2, Agg), new("string_agg", 2, Agg),
        new("glob", 2, C), new("like", 2, C), new("like", 3, C),
        // SQLITE_ENABLE_MATH_FUNCTIONS
        new("ceil", 1, C), new("ceiling", 1, C), new("floor", 1, C), new("trunc", 1, C), new("ln", 1, C),
        new("log", 1, C), new("log10", 1, C), new("log2", 1, C), new("log", 2, C), new("exp", 1, C),
        new("pow", 2, C), new("power", 2, C), new("mod", 2, C), new("acos", 1, C), new("asin", 1, C),
        new("atan", 1, C), new("atan2", 2, C), new("cos", 1, C), new("sin", 1, C), new("tan", 1, C),
        new("cosh", 1, C), new("sinh", 1, C), new("tanh", 1, C), new("acosh", 1, C), new("asinh", 1, C),
        new("atanh", 1, C), new("sqrt", 1, C), new("radians", 1, C), new("degrees", 1, C), new("pi", 0, C),
        new("sign", 1, C), new("coalesce", -4, C), new("iif", -4, C), new("if", -4, C),

        // window.c: https://github.com/sqlite/sqlite/blob/version-3.53.4/src/window.c#L610 (sqlite3WindowFunctions)
        new("row_number", 0, Win), new("dense_rank", 0, Win), new("rank", 0, Win), new("percent_rank", 0, Win),
        new("cume_dist", 0, Win), new("ntile", 1, Win), new("last_value", 1, Win), new("nth_value", 2, Win),
        new("first_value", 1, Win), new("lead", 1, Win), new("lead", 2, Win), new("lead", 3, Win),
        new("lag", 1, Win), new("lag", 2, Win), new("lag", 3, Win),

        // date.c: PURE_DATE is deterministic and changes slowly, the current_* functions only change slowly
        // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/date.c#L1805 (sqlite3RegisterDateTimeFunctions)
        new("julianday", -1, PureDate), new("unixepoch", -1, PureDate), new("date", -1, PureDate),
        new("time", -1, PureDate), new("datetime", -1, PureDate), new("strftime", -1, PureDate),
        new("timediff", 2, PureDate), new("current_time", 0, D), new("current_timestamp", 0, D),
        new("current_date", 0, D),

        // json.c, all deterministic: https://github.com/sqlite/sqlite/blob/version-3.53.4/src/json.c#L5656 (sqlite3RegisterJsonFunctions)
        new("json", 1, C), new("jsonb", 1, C), new("json_array", -1, C), new("jsonb_array", -1, C),
        new("json_array_insert", -1, C), new("jsonb_array_insert", -1, C), new("json_array_length", 1, C),
        new("json_array_length", 2, C), new("json_error_position", 1, C), new("json_extract", -1, C),
        new("jsonb_extract", -1, C), new("->", 2, C), new("->>", 2, C), new("json_insert", -1, C),
        new("jsonb_insert", -1, C), new("json_object", -1, C), new("jsonb_object", -1, C), new("json_patch", 2, C),
        new("jsonb_patch", 2, C), new("json_pretty", 1, C), new("json_pretty", 2, C), new("json_quote", 1, C),
        new("json_remove", -1, C), new("jsonb_remove", -1, C), new("json_replace", -1, C),
        new("jsonb_replace", -1, C), new("json_set", -1, C), new("jsonb_set", -1, C), new("json_type", 1, C),
        new("json_type", 2, C), new("json_valid", 1, C), new("json_valid", 2, C),
        new("json_group_array", 1, Agg | C), new("jsonb_group_array", 1, Agg | C),
        new("json_group_object", 2, Agg | C), new("jsonb_group_object", 2, Agg | C),
    };

    private static readonly Dictionary<string, List<Function>> ByName = BuildIndex();

    private static Dictionary<string, List<Function>> BuildIndex()
    {
        Dictionary<string, List<Function>> byName = new Dictionary<string, List<Function>>(AsciiCaseInsensitiveComparer.Instance);
        foreach (Function function in All)
        {
            if (!byName.TryGetValue(function.Name, out List<Function>? list))
                byName[function.Name] = list = new List<Function>();
            list.Add(function);
        }

        return byName;
    }

    /// <summary>
    /// sqlite3FindFunction in callback.c: the function with this name that takes <paramref name="argCount"/>
    /// arguments, preferring one with exactly that many over one that takes any number. -2 finds any function with
    /// the name.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/callback.c#L299 (matchQuality)
    /// </summary>
    public static bool TryFind(string name, int argCount, out Function function)
    {
        function = default;
        if (!ByName.TryGetValue(name, out List<Function>? candidates))
            return false;

        int best = 0;
        foreach (Function candidate in candidates)
        {
            int match;
            if (candidate.ArgCount == argCount)
                match = 4;
            else if (argCount == -2)
                match = 4;
            else if (candidate.ArgCount >= 0 || (candidate.ArgCount < -2 && argCount < -2 - candidate.ArgCount))
                match = 0;
            else
                match = 1;

            if (match > best)
            {
                best = match;
                function = candidate;
            }
        }

        return best > 0;
    }
}
