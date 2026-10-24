using System.Collections.Generic;
using MBW.Utilities.ManagedSqlite.Core.Helpers;

namespace MBW.Utilities.ManagedSqlite.Sql.Internal.Grammar;

/// <summary>
/// The actions of parse.y's rules, by rule. The left-hand side of a rule takes the stack entry of its first right-hand
/// symbol, so a rule without a case here passes that symbol's value on, which is what parse.y's rules without an
/// action, or with "A ::= B(A)", do. An empty rule starts from a cleared entry: no token, a null value and 0.
///
/// Only what reading a table needs is built: the CREATE TABLE, its expressions, and enough of a SELECT (in a
/// subquery) for the checks the parser makes. The other CREATE statements are checked against the schema when the whole
/// schema is loaded (SchemaParser.Schema.cs); any other statement is parsed and ignored.
/// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y
/// </summary>
internal sealed partial class SchemaParser
{
    // SF_* in sqliteInt.h, for the distinct nonterminal
    private const int SF_Distinct = 0x0000001, SF_All = 0x0000002;

    /// <summary>
    /// SQLITE_MAX_VARIABLE_NUMBER, the default for SQLITE_LIMIT_VARIABLE_NUMBER
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/sqliteLimit.h#L190
    /// </summary>
    private const int MaxVariableNumber = 32766;

    /// <summary>
    /// pParse->nVar and pParse->pVList: the parameters numbered so far
    /// </summary>
    private int _variableCount;

    private readonly Dictionary<string, int> _variableNames = new Dictionary<string, int>();

    private void Reduce(int rule, int lhs)
    {
        ref StackEntry a = ref _stack[lhs];

        Token T(int i) => _stack[lhs + i].Token;
        int I(int i) => _stack[lhs + i].Int;
        int M(int i) => _stack[lhs + i].Major;
        Expr E(int i) => (Expr)_stack[lhs + i].Value!;
        Expr? OE(int i) => (Expr?)_stack[lhs + i].Value;
        ExprList? L(int i) => (ExprList?)_stack[lhs + i].Value;
        Select S(int i) => (Select)_stack[lhs + i].Value!;
        Window W(int i) => (Window)_stack[lhs + i].Value!;

        switch (rule)
        {
            // ///////////////////// The CREATE TABLE statement

            case SqliteGrammar.Rule.cmdx__cmd:
                // sqlite3FinishCoding: at init this ends the parse (SQLITE_DONE), so nothing after the first
                // statement is read (DDL-11)
                _done = true;
                break;

            case SqliteGrammar.Rule.create_table__createkw_temp_TABLE_ifnotexists_nm_dbnm:
                StartTable(T(4), T(5), I(3) != 0);
                break;

            // ///////////////////// The other CREATE statements of the schema

            case SqliteGrammar.Rule.cmd__createkw_uniqueflag_INDEX_ifnotexists_nm_dbnm_ON_nm_LP_sortlist_RP_where_opt:
                CreateIndexStatement(T(4), T(5), T(7), L(9)!, OE(11), I(3) != 0);
                break;
            case SqliteGrammar.Rule.cmd__createkw_temp_VIEW_ifnotexists_nm_dbnm_eidlist_opt_AS_select:
                CreateView(T(4), T(5), I(3) != 0);
                break;

            case SqliteGrammar.Rule.trigger_decl__temp_TRIGGER_ifnotexists_nm_dbnm_trigger_time_trigger_event_ON_fullname_foreach_clause_when_clause:
                BeginTrigger(T(3), T(4), I(5), T(8), I(2) != 0);
                break;
            case SqliteGrammar.Rule.cmd__createkw_trigger_decl_BEGIN_trigger_cmd_list_END:
                FinishTrigger();
                break;
            case SqliteGrammar.Rule.trigger_time__BEFORE_or_AFTER:
                a.Int = M(0);
                break;
            case SqliteGrammar.Rule.trigger_time__INSTEAD_OF:
                a.Int = TK.INSTEAD;
                break;
            case SqliteGrammar.Rule.trigger_time__empty:
                a.Int = TK.BEFORE;
                break;

            // The table of a FROM, DROP, ALTER or trigger: the token is its name, and the int says it names a database
            case SqliteGrammar.Rule.fullname__nm:
            case SqliteGrammar.Rule.xfullname__nm:
            case SqliteGrammar.Rule.xfullname__nm_AS_nm:
                a.Int = 0;
                break;
            case SqliteGrammar.Rule.fullname__nm_DOT_nm:
            case SqliteGrammar.Rule.xfullname__nm_DOT_nm:
            case SqliteGrammar.Rule.xfullname__nm_DOT_nm_AS_nm:
                a.Token = T(2);
                a.Int = 1;
                break;

            case SqliteGrammar.Rule.tridxby__INDEXED_BY_nm:
                Fail("DDL-82", "the INDEXED BY clause is not allowed on UPDATE or DELETE statements within triggers");
                break;
            case SqliteGrammar.Rule.tridxby__NOT_INDEXED:
                Fail("DDL-82", "the NOT INDEXED clause is not allowed on UPDATE or DELETE statements within triggers");
                break;
            case SqliteGrammar.Rule.trigger_cmd__UPDATE_orconf_xfullname_tridxby_SET_setlist_from_where_opt_scanpt:
            case SqliteGrammar.Rule.trigger_cmd__DELETE_FROM_xfullname_tridxby_where_opt_scanpt:
                TriggerStep(I(2) != 0);
                break;
            case SqliteGrammar.Rule.trigger_cmd__scanpt_insert_cmd_INTO_xfullname_idlist_opt_select_upsert_scanpt:
                TriggerStep(I(3) != 0);

                // sqlite3TriggerInsertStep in trigger.c: the first ON CONFLICT target may not have NULLS
                //     if( pUpsert ){
                //       sqlite3HasExplicitNulls(pParse, pUpsert->pUpsertTarget);
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/trigger.c#L540 (sqlite3TriggerInsertStep)
                HasExplicitNulls(L(6));
                break;

            // An upsert's value is the ON CONFLICT target of its first clause, which comes first in the chain
            case SqliteGrammar.Rule.upsert__ON_CONFLICT_LP_sortlist_RP_where_opt_DO_UPDATE_SET_setlist_where_opt_upsert:
            case SqliteGrammar.Rule.upsert__ON_CONFLICT_LP_sortlist_RP_where_opt_DO_NOTHING_upsert:
                a.Value = _stack[lhs + 3].Value;
                break;
            case SqliteGrammar.Rule.upsert__ON_CONFLICT_DO_NOTHING_returning:
            case SqliteGrammar.Rule.upsert__ON_CONFLICT_DO_UPDATE_SET_setlist_where_opt_returning:
                a.Value = null;
                break;

            // sqlite3ExprListAppendVector in expr.c: "SET (a, b) = ..." needs as many values as names, unless the value
            // is a subquery
            //   if( pExpr->op!=TK_SELECT && pColumns->nId!=(n=sqlite3ExprVectorSize(pExpr)) ){
            //     sqlite3ErrorMsg(pParse, "%d columns assigned %d values",
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L2113 (sqlite3ExprListAppendVector)
            case SqliteGrammar.Rule.setlist__setlist_COMMA_LP_idlist_RP_EQ_expr:
                AppendVector(I(3), E(6));
                break;
            case SqliteGrammar.Rule.setlist__LP_idlist_RP_EQ_expr:
                AppendVector(I(1), E(4));
                break;
            case SqliteGrammar.Rule.idlist__nm:
                a.Int = 1;
                break;
            case SqliteGrammar.Rule.idlist__idlist_COMMA_nm:
                a.Int = I(0) + 1;
                break;

            // sqlite3AddReturning in build.c
            //   if( pParse->pNewTrigger ){
            //     sqlite3ErrorMsg(pParse, "cannot use RETURNING in a trigger");
            // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1439 (sqlite3AddReturning)
            case SqliteGrammar.Rule.returning__RETURNING_selcollist:
            case SqliteGrammar.Rule.upsert__RETURNING_selcollist:
                a.Value = null;
                goto case SqliteGrammar.Rule.where_opt_ret__RETURNING_selcollist;
            case SqliteGrammar.Rule.where_opt_ret__RETURNING_selcollist:
            case SqliteGrammar.Rule.where_opt_ret__WHERE_expr_RETURNING_selcollist:
                if (_newTrigger != null)
                    Fail("DDL-82", "cannot use RETURNING in a trigger");
                break;

            case SqliteGrammar.Rule.create_vtab__createkw_VIRTUAL_TABLE_ifnotexists_nm_dbnm_USING_nm:
                BeginVirtualTable(T(4), T(5), I(3) != 0);
                break;
            case SqliteGrammar.Rule.cmd__create_vtab:
            case SqliteGrammar.Rule.cmd__create_vtab_LP_vtabarglist_RP:
                FinishVirtualTable();
                break;
            case SqliteGrammar.Rule.vtabarg__empty:
                VirtualTableArgument(false);
                break;
            case SqliteGrammar.Rule.vtabargtoken__ANY:
            case SqliteGrammar.Rule.vtabargtoken__lp_anylist_RP:
            case SqliteGrammar.Rule.lp__LP:
                VirtualTableArgument(true);
                break;
            case SqliteGrammar.Rule.ifnotexists__IF_NOT_EXISTS:
                a.Int = 1;
                break;
            case SqliteGrammar.Rule.temp__TEMP:
                // A = pParse->db->init.busy==0: TEMP is ignored while the schema is read (SCHEMA-29)
                a.Int = 0;
                break;
            case SqliteGrammar.Rule.create_table_args__LP_columnlist_conslist_opt_RP_table_option_set:
                EndTable(false, I(4));
                break;
            case SqliteGrammar.Rule.create_table_args__AS_select:
                EndTable(true, 0);
                break;
            case SqliteGrammar.Rule.table_option_set__table_option_set_COMMA_table_option:
                a.Int = I(0) | I(2);
                break;
            case SqliteGrammar.Rule.table_option__WITHOUT_nm:
                // The raw token, so a quoted "rowid" is not the option
                if (T(1).Length == 5 && AsciiCaseInsensitiveComparer.Instance.Equals(Text(T(1)), "rowid"))
                    a.Int = ParsedTable.TF_WithoutRowid;
                else
                    Fail("DDL-29", $"unknown table option: {Text(T(1))}");
                break;
            case SqliteGrammar.Rule.table_option__nm:
                if (T(0).Length == 6 && AsciiCaseInsensitiveComparer.Instance.Equals(Text(T(0)), "strict"))
                    a.Int = ParsedTable.TF_Strict;
                else
                    Fail("DDL-29", $"unknown table option: {Text(T(0))}");
                break;
            case SqliteGrammar.Rule.columnname__nm_typetoken:
                AddColumn(T(0), T(1));
                break;

            case SqliteGrammar.Rule.typetoken__typename_LP_signed_RP:
                a.Token = new Token(T(0).Start, T(3).End - T(0).Start);
                break;
            case SqliteGrammar.Rule.typetoken__typename_LP_signed_COMMA_signed_RP:
                a.Token = new Token(T(0).Start, T(5).End - T(0).Start);
                break;
            case SqliteGrammar.Rule.typename__typename_ID_or_STRING:
                a.Token = new Token(T(0).Start, T(1).End - T(0).Start);
                break;
            case SqliteGrammar.Rule.scanpt__empty:
                a.Int = _lookahead.Start;
                break;
            case SqliteGrammar.Rule.scantok__empty:
                a.Token = _lookahead;
                break;

            case SqliteGrammar.Rule.ccons__DEFAULT_scantok_term:
                AddDefaultValue(E(2), T(1).Start, T(1).End);
                break;
            case SqliteGrammar.Rule.ccons__DEFAULT_LP_expr_RP:
                AddDefaultValue(E(2), T(1).Start + 1, T(3).Start);
                break;
            case SqliteGrammar.Rule.ccons__DEFAULT_PLUS_scantok_term:
                AddDefaultValue(E(3), T(1).Start, T(2).End);
                break;
            case SqliteGrammar.Rule.ccons__DEFAULT_MINUS_scantok_term:
                AddDefaultValue(PExpr(TK.UMINUS, E(3), null), T(1).Start, T(2).End);
                break;
            case SqliteGrammar.Rule.ccons__DEFAULT_scantok_ID_or_INDEXED:
            {
                Expr p = TokenExpr(TK.STRING, T(2));
                IdToTrueFalse(p);
                AddDefaultValue(p, T(2).Start, T(2).End);
                break;
            }
            case SqliteGrammar.Rule.ccons__NOT_NULL_onconf:
                AddNotNull(I(2));
                break;
            case SqliteGrammar.Rule.ccons__PRIMARY_KEY_sortorder_onconf_autoinc:
                AddPrimaryKey(null, I(3), I(4), I(2));
                break;
            case SqliteGrammar.Rule.ccons__UNIQUE_onconf:
                CreateIndex(null, I(1), SO_ASC, false);
                break;
            case SqliteGrammar.Rule.ccons__CHECK_LP_expr_RP:
            case SqliteGrammar.Rule.tcons__CHECK_LP_expr_RP_onconf:
                // sqlite3AddCheckConstraint: dropped, as SQLite drops a CHECK when the database is read-only (DDL-46,
                // https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L1911), so a CHECK naming no such
                // column is accepted, as by a database opened with SQLITE_OPEN_READONLY. The tests' SQLite can not
                // show this: a database it deserializes is read-only to writes only, and so resolves the CHECK.
                break;
            case SqliteGrammar.Rule.ccons__REFERENCES_nm_eidlist_opt_refargs:
                CreateForeignKey(null, T(1), L(2));
                break;
            case SqliteGrammar.Rule.ccons__COLLATE_ID_or_STRING:
                AddCollateType(T(1));
                break;
            case SqliteGrammar.Rule.generated__LP_expr_RP:
                AddGenerated(E(1), null);
                break;
            case SqliteGrammar.Rule.generated__LP_expr_RP_ID:
                AddGenerated(E(1), T(3));
                break;
            case SqliteGrammar.Rule.autoinc__AUTOINCR:
                a.Int = 1;
                break;

            case SqliteGrammar.Rule.tcons__PRIMARY_KEY_LP_sortlist_autoinc_RP_onconf:
                AddPrimaryKey(L(3), I(6), I(4), SO_ASC);
                break;
            case SqliteGrammar.Rule.tcons__UNIQUE_LP_sortlist_RP_onconf:
                CreateIndex(L(2), I(4), SO_ASC, false);
                break;
            case SqliteGrammar.Rule.tcons__FOREIGN_KEY_LP_eidlist_RP_REFERENCES_nm_eidlist_opt_refargs_defer_subclause_opt:
                CreateForeignKey(L(3), T(6), L(7));
                break;

            case SqliteGrammar.Rule.onconf__empty:
            case SqliteGrammar.Rule.orconf__empty:
                a.Int = OE_Default;
                break;
            case SqliteGrammar.Rule.onconf__ON_CONFLICT_resolvetype:
                a.Int = I(2);
                break;
            case SqliteGrammar.Rule.orconf__OR_resolvetype:
                a.Int = I(1);
                break;
            case SqliteGrammar.Rule.resolvetype__IGNORE:
                a.Int = OE_Ignore;
                break;
            case SqliteGrammar.Rule.resolvetype__REPLACE:
                a.Int = OE_Replace;
                break;
            case SqliteGrammar.Rule.raisetype__ROLLBACK:
                a.Int = OE_Rollback;
                break;
            case SqliteGrammar.Rule.raisetype__ABORT:
                a.Int = OE_Abort;
                break;
            case SqliteGrammar.Rule.raisetype__FAIL:
                a.Int = OE_Fail;
                break;

            // ///////////////////// The SELECT statement, for subqueries

            case SqliteGrammar.Rule.select__WITH_wqlist_selectnowith:
                DoubleLinkSelect(S(2));
                a.Value = S(2);
                break;
            case SqliteGrammar.Rule.select__WITH_RECURSIVE_wqlist_selectnowith:
                DoubleLinkSelect(S(3));
                a.Value = S(3);
                break;
            case SqliteGrammar.Rule.select__selectnowith:
                DoubleLinkSelect(S(0));
                break;
            case SqliteGrammar.Rule.selectnowith__selectnowith_multiselect_op_oneselect:
            {
                Select left = S(0);
                Select right = S(2);
                if (right.Prior != null)
                {
                    // The right side, itself a compound of VALUES rows, becomes a subquery
                    DoubleLinkSelect(right);
                    right = new Select { Height = 1 };
                }

                right.Op = I(1);
                right.Prior = left;
                left.IsMultiValue = false;
                right.IsMultiValue = false;
                a.Value = right;
                break;
            }
            case SqliteGrammar.Rule.multiselect_op__UNION:
            case SqliteGrammar.Rule.multiselect_op__EXCEPT_or_INTERSECT:
                a.Int = M(0);
                break;
            case SqliteGrammar.Rule.multiselect_op__UNION_ALL:
                a.Int = TK.ALL;
                break;
            case SqliteGrammar.Rule.oneselect__SELECT_distinct_selcollist_from_where_opt_groupby_opt_having_opt_orderby_opt_limit_opt:
                a.Value = NewSelect(L(2), OE(4), L(5), OE(6), L(7), OE(8));
                break;
            case SqliteGrammar.Rule.oneselect__SELECT_distinct_selcollist_from_where_opt_groupby_opt_having_opt_window_clause_orderby_opt_limit_opt:
                a.Value = NewSelect(L(2), OE(4), L(5), OE(6), L(8), OE(9));
                break;
            case SqliteGrammar.Rule.values__VALUES_LP_nexprlist_RP:
            {
                Select values = NewSelect(L(2), null, null, null, null, null);
                values.IsValues = true;
                a.Value = values;
                break;
            }
            case SqliteGrammar.Rule.mvalues__values_COMMA_LP_nexprlist_RP:
            case SqliteGrammar.Rule.mvalues__mvalues_COMMA_LP_nexprlist_RP:
                a.Value = MultiValues(S(0), L(3)!);
                break;
            case SqliteGrammar.Rule.distinct__DISTINCT:
                a.Int = SF_Distinct;
                break;
            case SqliteGrammar.Rule.distinct__ALL:
                a.Int = SF_All;
                break;

            case SqliteGrammar.Rule.selcollist__sclp_scanpt_expr_scanpt_as:
                a.Value = ListAppend(L(0), E(2));
                break;
            case SqliteGrammar.Rule.selcollist__sclp_scanpt_STAR:
                a.Value = ListAppend(L(0), new Expr(TK.ASTERISK));
                break;
            case SqliteGrammar.Rule.selcollist__sclp_scanpt_nm_DOT_STAR:
                a.Value = ListAppend(L(0), PExpr(TK.DOT, TokenExpr(TK.ID, T(2)), new Expr(TK.ASTERISK)));
                break;
            case SqliteGrammar.Rule.as__AS_nm:
                a.Token = T(1);
                break;
            case SqliteGrammar.Rule.from__FROM_seltablist:
                a.Value = _stack[lhs + 1].Value;
                break;
            case SqliteGrammar.Rule.seltablist__stl_prefix_nm_dbnm_as_on_using:
                FromDatabase(T(1), T(2));
                a.Value = AppendFromTerm((SrcList?)a.Value, I(4));
                break;
            case SqliteGrammar.Rule.seltablist__stl_prefix_nm_dbnm_as_indexed_by_on_using:
                FromDatabase(T(1), T(2));
                a.Value = AppendFromTerm((SrcList?)a.Value, I(5));
                break;
            case SqliteGrammar.Rule.seltablist__stl_prefix_nm_dbnm_LP_exprlist_RP_as_on_using:
                FromDatabase(T(1), T(2));
                a.Value = AppendFromTerm((SrcList?)a.Value, I(7));
                break;
            case SqliteGrammar.Rule.seltablist__stl_prefix_LP_select_RP_as_on_using:
                a.Value = AppendFromTerm((SrcList?)a.Value, I(5));
                break;
            case SqliteGrammar.Rule.seltablist__stl_prefix_LP_seltablist_RP_as_on_using:
                if (a.Value == null && T(4).Length == 0 && I(5) == 0)
                    a.Value = _stack[lhs + 2].Value;
                else
                    a.Value = AppendFromTerm((SrcList?)a.Value, I(5));
                break;
            case SqliteGrammar.Rule.dbnm__DOT_nm:
                a.Token = T(1);
                break;
            case SqliteGrammar.Rule.joinop__JOIN_KW_JOIN:
                JoinType(T(0), null, null);
                break;
            case SqliteGrammar.Rule.joinop__JOIN_KW_nm_JOIN:
                JoinType(T(0), T(1), null);
                break;
            case SqliteGrammar.Rule.joinop__JOIN_KW_nm_nm_JOIN:
                JoinType(T(0), T(1), T(2));
                break;
            case SqliteGrammar.Rule.on_using__ON_expr:
                a.Int = 1;
                break;
            case SqliteGrammar.Rule.on_using__USING_LP_idlist_RP:
                a.Int = 2;
                break;

            case SqliteGrammar.Rule.orderby_opt__ORDER_BY_sortlist:
            case SqliteGrammar.Rule.groupby_opt__GROUP_BY_nexprlist:
                a.Value = _stack[lhs + 2].Value;
                break;
            case SqliteGrammar.Rule.sortlist__sortlist_COMMA_expr_sortorder_nulls:
                a.Value = ListAppend(L(0), E(2));
                SetSortOrder(L(0)!, I(3), I(4));
                break;
            case SqliteGrammar.Rule.sortlist__expr_sortorder_nulls:
            {
                ExprList list = ListAppend(null, E(0));
                SetSortOrder(list, I(1), I(2));
                a.Value = list;
                break;
            }
            case SqliteGrammar.Rule.sortorder__ASC:
            case SqliteGrammar.Rule.nulls__NULLS_FIRST:
                a.Int = SO_ASC;
                break;
            case SqliteGrammar.Rule.sortorder__DESC:
            case SqliteGrammar.Rule.nulls__NULLS_LAST:
                a.Int = SO_DESC;
                break;
            case SqliteGrammar.Rule.sortorder__empty:
            case SqliteGrammar.Rule.nulls__empty:
                a.Int = SO_UNDEFINED;
                break;
            case SqliteGrammar.Rule.having_opt__HAVING_expr:
            case SqliteGrammar.Rule.where_opt__WHERE_expr:
                a.Value = _stack[lhs + 1].Value;
                break;
            case SqliteGrammar.Rule.limit_opt__LIMIT_expr:
                a.Value = PExpr(TK.LIMIT, E(1), null);
                break;
            case SqliteGrammar.Rule.limit_opt__LIMIT_expr_OFFSET_expr:
                a.Value = PExpr(TK.LIMIT, E(1), E(3));
                break;
            case SqliteGrammar.Rule.limit_opt__LIMIT_expr_COMMA_expr:
                a.Value = PExpr(TK.LIMIT, E(3), E(1));
                break;

            // ///////////////////// Expressions

            case SqliteGrammar.Rule.expr__LP_expr_RP:
                a.Value = _stack[lhs + 1].Value;
                break;
            case SqliteGrammar.Rule.expr__ID_or_INDEXED_or_JOIN_KW:
                a.Value = TokenExpr(TK.ID, T(0));
                break;
            case SqliteGrammar.Rule.expr__nm_DOT_nm:
                a.Value = PExpr(TK.DOT, TokenExpr(TK.ID, T(0)), TokenExpr(TK.ID, T(2)));
                break;
            case SqliteGrammar.Rule.expr__nm_DOT_nm_DOT_nm:
            {
                Expr first = TokenExpr(TK.ID, T(0));
                Expr rest = PExpr(TK.DOT, TokenExpr(TK.ID, T(2)), TokenExpr(TK.ID, T(4)));
                a.Value = PExpr(TK.DOT, first, rest);
                break;
            }
            case SqliteGrammar.Rule.term__NULL_or_FLOAT_or_BLOB:
                a.Value = TokenExpr(M(0), T(0));
                break;
            case SqliteGrammar.Rule.term__STRING:
                a.Value = TokenExpr(TK.STRING, T(0));
                break;
            case SqliteGrammar.Rule.term__INTEGER:
                a.Value = IntegerTerm(T(0));
                break;
            case SqliteGrammar.Rule.term__QNUMBER:
                a.Value = NumberTerm(T(0));
                break;
            case SqliteGrammar.Rule.expr__VARIABLE:
                a.Value = Variable(T(0));
                break;
            case SqliteGrammar.Rule.expr__expr_COLLATE_ID_or_STRING:
                a.Value = AddCollate(E(0), T(2));
                break;
            case SqliteGrammar.Rule.expr__CAST_LP_expr_AS_typetoken_RP:
            {
                // sqlite3ExprAttachSubtrees, which takes the operand's height without checking it
                Expr operand = E(2);
                a.Value = new Expr(TK.CAST)
                {
                    Text = SqliteTokenizer.Dequote(Text(T(4))),
                    Left = operand,
                    Flags = Expr.EP_Propagate & operand.Flags,
                    Height = operand.Height + 1
                };
                break;
            }
            case SqliteGrammar.Rule.expr__ID_or_INDEXED_or_JOIN_KW_LP_distinct_exprlist_RP:
                a.Value = Function(L(3), T(0), I(2) == SF_Distinct);
                break;
            case SqliteGrammar.Rule.expr__ID_or_INDEXED_or_JOIN_KW_LP_distinct_exprlist_ORDER_BY_sortlist_RP:
            {
                Expr function = Function(L(3), T(0), I(2) == SF_Distinct);
                AddFunctionOrderBy(function, L(6)!);
                a.Value = function;
                break;
            }
            case SqliteGrammar.Rule.expr__ID_or_INDEXED_or_JOIN_KW_LP_STAR_RP:
                a.Value = Function(null, T(0), false);
                break;
            case SqliteGrammar.Rule.expr__ID_or_INDEXED_or_JOIN_KW_LP_distinct_exprlist_RP_filter_over:
            {
                Expr function = Function(L(3), T(0), I(2) == SF_Distinct);
                WindowAttach(function, W(5));
                a.Value = function;
                break;
            }
            case SqliteGrammar.Rule.expr__ID_or_INDEXED_or_JOIN_KW_LP_distinct_exprlist_ORDER_BY_sortlist_RP_filter_over:
            {
                Expr function = Function(L(3), T(0), I(2) == SF_Distinct);
                WindowAttach(function, W(8));
                AddFunctionOrderBy(function, L(6)!);
                a.Value = function;
                break;
            }
            case SqliteGrammar.Rule.expr__ID_or_INDEXED_or_JOIN_KW_LP_STAR_RP_filter_over:
            {
                Expr function = Function(null, T(0), false);
                WindowAttach(function, W(4));
                a.Value = function;
                break;
            }
            case SqliteGrammar.Rule.term__CTIME_KW:
                a.Value = Function(null, T(0), false);
                break;
            case SqliteGrammar.Rule.expr__LP_nexprlist_COMMA_expr_RP:
            {
                // A row value. Its height stays 1, as SQLite does not set it from the list.
                ExprList list = ListAppend(L(1), E(3));
                Expr vector = PExpr(TK.VECTOR, null, null);
                vector.List = list;
                foreach (ExprListItem item in list)
                    vector.Flags |= item.Expr!.Flags & Expr.EP_Propagate;
                a.Value = vector;
                break;
            }
            case SqliteGrammar.Rule.expr__expr_AND_expr:
                a.Value = ExprAnd(E(0), E(2));
                break;
            case SqliteGrammar.Rule.expr__expr_OR_expr:
            case SqliteGrammar.Rule.expr__expr_LT_or_GT_or_GE_or_LE_expr:
            case SqliteGrammar.Rule.expr__expr_EQ_or_NE_expr:
            case SqliteGrammar.Rule.expr__expr_BITAND_or_BITOR_or_LSHIFT_or_RSHIFT_expr:
            case SqliteGrammar.Rule.expr__expr_PLUS_or_MINUS_expr:
            case SqliteGrammar.Rule.expr__expr_STAR_or_SLASH_or_REM_expr:
            case SqliteGrammar.Rule.expr__expr_CONCAT_expr:
                a.Value = PExpr(M(1), E(0), E(2));
                break;
            case SqliteGrammar.Rule.likeop__NOT_LIKE_KW_or_MATCH:
                a.Token = T(1);
                a.Int = 1;
                break;
            case SqliteGrammar.Rule.expr__expr_likeop_expr:
            case SqliteGrammar.Rule.expr__expr_likeop_expr_ESCAPE_expr:
            {
                // x LIKE y is like(y, x), and x LIKE y ESCAPE z is like(y, x, z)
                ExprList list = ListAppend(null, E(2));
                ListAppend(list, E(0));
                if (SqliteGrammar.RuleRhsCount[rule] == -5)
                    ListAppend(list, E(4));
                Expr p = Function(list, T(1), false);
                if (I(1) != 0)
                    p = PExpr(TK.NOT, p, null);
                p.Flags |= Expr.EP_InfixFunc;
                a.Value = p;
                break;
            }
            case SqliteGrammar.Rule.expr__expr_ISNULL_or_NOTNULL:
                a.Value = PExprIsNull(M(1), E(0));
                break;
            case SqliteGrammar.Rule.expr__expr_NOT_NULL:
                a.Value = PExprIsNull(TK.NOTNULL, E(0));
                break;
            case SqliteGrammar.Rule.expr__expr_IS_expr:
                a.Value = PExprIs(TK.IS, E(0), E(2));
                break;
            case SqliteGrammar.Rule.expr__expr_IS_NOT_expr:
                a.Value = PExprIs(TK.ISNOT, E(0), E(3));
                break;
            case SqliteGrammar.Rule.expr__expr_IS_NOT_DISTINCT_FROM_expr:
                a.Value = PExprIs(TK.IS, E(0), E(5));
                break;
            case SqliteGrammar.Rule.expr__expr_IS_DISTINCT_FROM_expr:
                a.Value = PExprIs(TK.ISNOT, E(0), E(4));
                break;
            case SqliteGrammar.Rule.expr__NOT_expr:
            case SqliteGrammar.Rule.expr__BITNOT_expr:
                a.Value = PExpr(M(0), E(1), null);
                break;
            case SqliteGrammar.Rule.expr__PLUS_or_MINUS_expr:
            {
                int op = M(0) + (TK.UPLUS - TK.PLUS);
                Expr operand = E(1);
                if (operand.Op == TK.UPLUS)
                {
                    operand.Op = op;
                    a.Value = operand;
                }
                else
                {
                    a.Value = PExpr(op, operand, null);
                }

                break;
            }
            case SqliteGrammar.Rule.expr__expr_PTR_expr:
            {
                ExprList list = ListAppend(null, E(0));
                ListAppend(list, E(2));
                a.Value = Function(list, T(1), false);
                break;
            }
            case SqliteGrammar.Rule.between_op__NOT_BETWEEN:
            case SqliteGrammar.Rule.in_op__NOT_IN:
                a.Int = 1;
                break;
            case SqliteGrammar.Rule.expr__expr_between_op_expr_AND_expr:
            {
                ExprList list = ListAppend(null, E(2));
                ListAppend(list, E(4));
                Expr p = PExpr(TK.BETWEEN, E(0), null);
                p.List = list;
                SetHeightAndFlags(p);
                if (I(1) != 0)
                    p = PExpr(TK.NOT, p, null);
                a.Value = p;
                break;
            }
            case SqliteGrammar.Rule.expr__expr_in_op_LP_exprlist_RP:
                a.Value = InList(E(0), I(1) != 0, L(3));
                break;
            case SqliteGrammar.Rule.expr__LP_select_RP:
            {
                Expr p = PExpr(TK.SELECT, null, null);
                AddSelect(p, S(1));
                a.Value = p;
                break;
            }
            case SqliteGrammar.Rule.expr__expr_in_op_LP_select_RP:
            {
                Expr p = PExpr(TK.IN, E(0), null);
                AddSelect(p, S(3));
                a.Value = I(1) != 0 ? PExpr(TK.NOT, p, null) : p;
                break;
            }
            case SqliteGrammar.Rule.expr__expr_in_op_nm_dbnm_paren_exprlist:
            {
                // "x IN table" or "x IN function(args)", a SELECT * FROM it
                Expr p = PExpr(TK.IN, E(0), null);
                AddSelect(p, new Select { Height = 1 });
                a.Value = I(1) != 0 ? PExpr(TK.NOT, p, null) : p;
                break;
            }
            case SqliteGrammar.Rule.expr__EXISTS_LP_select_RP:
            {
                Expr p = PExpr(TK.EXISTS, null, null);
                AddSelect(p, S(2));
                a.Value = p;
                break;
            }
            case SqliteGrammar.Rule.expr__CASE_case_operand_case_exprlist_case_else_END:
            {
                Expr p = PExpr(TK.CASE, OE(1), null);
                p.List = OE(3) != null ? ListAppend(L(2), OE(3)) : L(2);
                SetHeightAndFlags(p);
                a.Value = p;
                break;
            }
            case SqliteGrammar.Rule.case_exprlist__case_exprlist_WHEN_expr_THEN_expr:
                ListAppend(L(0), E(2));
                ListAppend(L(0), E(4));
                break;
            case SqliteGrammar.Rule.case_exprlist__WHEN_expr_THEN_expr:
            {
                ExprList list = ListAppend(null, E(1));
                ListAppend(list, E(3));
                a.Value = list;
                break;
            }
            case SqliteGrammar.Rule.case_else__ELSE_expr:
                a.Value = _stack[lhs + 1].Value;
                break;
            case SqliteGrammar.Rule.nexprlist__nexprlist_COMMA_expr:
                ListAppend(L(0), E(2));
                break;
            case SqliteGrammar.Rule.nexprlist__expr:
                a.Value = ListAppend(null, E(0));
                break;
            case SqliteGrammar.Rule.paren_exprlist__LP_exprlist_RP:
            case SqliteGrammar.Rule.eidlist_opt__LP_eidlist_RP:
                a.Value = _stack[lhs + 1].Value;
                break;
            case SqliteGrammar.Rule.eidlist__eidlist_COMMA_nm_collate_sortorder:
                // parserAddExprIdListTerm: COLLATE and ASC or DESC are ignored in a legacy schema
                ListAppend(L(0), null)[L(0)!.Count - 1].Name = SqliteTokenizer.Dequote(Text(T(2)));
                break;
            case SqliteGrammar.Rule.eidlist__nm_collate_sortorder:
            {
                ExprList list = ListAppend(null, null);
                list[0].Name = SqliteTokenizer.Dequote(Text(T(0)));
                a.Value = list;
                break;
            }
            case SqliteGrammar.Rule.collate__COLLATE_ID_or_STRING:
                a.Int = 1;
                break;
            case SqliteGrammar.Rule.expr__RAISE_LP_IGNORE_RP:
                a.Value = PExpr(TK.RAISE, null, null);
                break;
            case SqliteGrammar.Rule.expr__RAISE_LP_raisetype_COMMA_expr_RP:
                a.Value = PExpr(TK.RAISE, E(4), null);
                break;

            // ///////////////////// WITH and window definitions, for subqueries

            case SqliteGrammar.Rule.wqlist__wqitem:
                a.Value = new List<string> { SqliteTokenizer.Dequote(Text(T(0))) };
                break;
            case SqliteGrammar.Rule.wqlist__wqlist_COMMA_wqitem:
            {
                // sqlite3WithAdd in build.c
                List<string> names = (List<string>)a.Value!;
                string name = SqliteTokenizer.Dequote(Text(T(2)));
                if (names.Exists(n => AsciiCaseInsensitiveComparer.Instance.Equals(n, name)))
                    Fail("SCHEMA-22", $"duplicate WITH table name: {name}");
                names.Add(name);
                break;
            }
            case SqliteGrammar.Rule.windowdefn_list__windowdefn_list_COMMA_windowdefn:
            {
                Window window = W(2);
                WindowChain(window, W(0));
                window.Next = W(0);
                a.Value = window;
                break;
            }
            case SqliteGrammar.Rule.windowdefn__nm_AS_LP_window_RP:
            {
                Window window = W(3);
                window.Name = Text(T(0));
                a.Value = window;
                break;
            }
            case SqliteGrammar.Rule.window__PARTITION_BY_nexprlist_orderby_opt_frame_opt:
                a.Value = WindowAssemble(W(4), true, L(3) != null, null);
                break;
            case SqliteGrammar.Rule.window__nm_PARTITION_BY_nexprlist_orderby_opt_frame_opt:
                a.Value = WindowAssemble(W(5), true, L(4) != null, T(0));
                break;
            case SqliteGrammar.Rule.window__ORDER_BY_sortlist_frame_opt:
                a.Value = WindowAssemble(W(3), false, true, null);
                break;
            case SqliteGrammar.Rule.window__nm_ORDER_BY_sortlist_frame_opt:
                a.Value = WindowAssemble(W(4), false, true, T(0));
                break;
            case SqliteGrammar.Rule.window__nm_frame_opt:
                a.Value = WindowAssemble(W(1), false, false, T(0));
                break;
            case SqliteGrammar.Rule.frame_opt__empty:
                a.Value = WindowAlloc(0, TK.UNBOUNDED, TK.CURRENT);
                break;
            case SqliteGrammar.Rule.frame_opt__range_or_rows_frame_bound_s_frame_exclude_opt:
                a.Value = WindowAlloc(I(0), I(1), TK.CURRENT);
                break;
            case SqliteGrammar.Rule.frame_opt__range_or_rows_BETWEEN_frame_bound_s_AND_frame_bound_e_frame_exclude_opt:
                a.Value = WindowAlloc(I(0), I(2), I(4));
                break;
            case SqliteGrammar.Rule.range_or_rows__RANGE_or_ROWS_or_GROUPS:
                a.Int = M(0);
                break;
            case SqliteGrammar.Rule.frame_bound_s__UNBOUNDED_PRECEDING:
            case SqliteGrammar.Rule.frame_bound_e__UNBOUNDED_FOLLOWING:
                a.Int = TK.UNBOUNDED;
                break;
            case SqliteGrammar.Rule.frame_bound__expr_PRECEDING_or_FOLLOWING:
                a.Int = M(1);
                break;
            case SqliteGrammar.Rule.frame_bound__CURRENT_ROW:
                a.Int = TK.CURRENT;
                break;
            case SqliteGrammar.Rule.window_clause__WINDOW_windowdefn_list:
            case SqliteGrammar.Rule.filter_over__filter_clause_over_clause:
                a.Value = _stack[lhs + 1].Value;
                break;
            case SqliteGrammar.Rule.filter_over__filter_clause:
                a.Value = new Window { FilterOnly = true };
                break;
            case SqliteGrammar.Rule.over_clause__OVER_LP_window_RP:
                a.Value = W(2);
                break;
            case SqliteGrammar.Rule.over_clause__OVER_nm:
                a.Value = new Window { Name = Text(T(1)) };
                break;
            case SqliteGrammar.Rule.filter_clause__FILTER_LP_WHERE_expr_RP:
                a.Value = _stack[lhs + 3].Value;
                break;
        }
    }

    /// <summary>
    /// sqlite3SelectNew in select.c, for what the checks need: the number of result columns and the expressions'
    /// greatest height
    /// </summary>
    private static Select NewSelect(ExprList? columns, Expr? where, ExprList? groupBy, Expr? having, ExprList? orderBy, Expr? limit)
    {
        int height = System.Math.Max(where?.Height ?? 0, having?.Height ?? 0);
        height = System.Math.Max(height, limit?.Height ?? 0);
        height = System.Math.Max(height, ListHeight(columns));
        height = System.Math.Max(height, ListHeight(groupBy));
        height = System.Math.Max(height, ListHeight(orderBy));

        return new Select
        {
            ColumnCount = columns?.Count ?? 1,
            Height = height,
            HasOrderBy = orderBy != null,
            HasLimit = limit != null
        };
    }

    /// <summary>
    /// sqlite3MultiValues in insert.c, at init: a VALUES row after the first becomes a UNION ALL term
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/insert.c#L679 (sqlite3MultiValues)
    /// </summary>
    private static Select MultiValues(Select left, ExprList row)
    {
        Select select = NewSelect(row, null, null, null, null, null);
        select.IsValues = true;
        select.IsMultiValue = left.Prior == null || left.IsMultiValue;
        select.Op = TK.ALL;
        select.Prior = left;
        left.IsMultiValue = false;
        return select;
    }

    /// <summary>
    /// parserDoubleLinkSelect in parse.y: in a compound SELECT only the last term may have ORDER BY or LIMIT, and a
    /// compound has at most 500 terms, unless it is one VALUES clause
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L543 (parserDoubleLinkSelect)
    /// </summary>
    private void DoubleLinkSelect(Select p)
    {
        if (p.Prior == null)
            return;

        int count = 1;
        Select next = p;
        for (Select? loop = p.Prior; loop != null; loop = loop.Prior)
        {
            count++;
            if (loop.HasOrderBy || loop.HasLimit)
            {
                Fail("SCHEMA-22", $"{(loop.HasOrderBy ? "ORDER BY" : "LIMIT")} clause should come after {SelectOpName(next.Op)} not before");
                break;
            }

            next = loop;
        }

        if (!p.IsValues && !p.IsMultiValue && count > MaxCompoundSelect)
            Fail("SCHEMA-22", "too many terms in compound SELECT");
    }

    /// <summary>
    /// sqlite3SelectOpName in select.c
    /// </summary>
    private static string SelectOpName(int op) => op switch
    {
        TK.ALL => "UNION ALL",
        TK.INTERSECT => "INTERSECT",
        TK.EXCEPT => "EXCEPT",
        _ => "UNION"
    };

    /// <summary>
    /// sqlite3SrcListAppend in build.c: with a second name, a FROM term's first name is its database. The first
    /// database that is not the main one is kept for the view or trigger that holds it (sqlite3FixSelect). This is the
    /// first in the text, where SQLite names the first its walk of the SELECT meets, so with two such databases the
    /// error can name the other one; it is rejected either way.
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L4879 (sqlite3SrcListAppend)
    /// </summary>
    private void FromDatabase(Token name, Token database)
    {
        if (database.Length == 0 || _foreignDatabase != null)
            return;

        string db = SqliteTokenizer.Dequote(Text(name));
        if (!AsciiCaseInsensitiveComparer.Instance.Equals(db, "main"))
            _foreignDatabase = db;
    }

    /// <summary>
    /// sqlite3SrcListAppendFromTerm in build.c: ON and USING need a join before them
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/build.c#L5068 (sqlite3SrcListAppendFromTerm)
    /// </summary>
    private SrcList AppendFromTerm(SrcList? list, int onUsing)
    {
        if (list == null && onUsing != 0)
            Fail("SCHEMA-22", $"a JOIN clause is required before {(onUsing == 1 ? "ON" : "USING")}");

        list ??= new SrcList();
        list.Count++;
        return list;
    }

    /// <summary>
    /// sqlite3JoinType in select.c: the words of a join, such as "LEFT OUTER" or "NATURAL CROSS"
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/select.c#L260 (sqlite3JoinType)
    /// </summary>
    private void JoinType(Token a, Token? b, Token? c)
    {
        // JT_* in sqliteInt.h
        const int JT_INNER = 0x01, JT_CROSS = 0x02, JT_NATURAL = 0x04, JT_LEFT = 0x08, JT_RIGHT = 0x10, JT_OUTER = 0x20, JT_ERROR = 0x80;
        (string Word, int Code)[] keywords =
        {
            ("natural", JT_NATURAL), ("left", JT_LEFT | JT_OUTER), ("outer", JT_OUTER), ("right", JT_RIGHT | JT_OUTER),
            ("full", JT_LEFT | JT_RIGHT | JT_OUTER), ("inner", JT_INNER), ("cross", JT_INNER | JT_CROSS),
        };

        int joinType = 0;
        foreach (Token? token in new[] { a, b, c })
        {
            if (token == null)
                break;

            string word = Text(token.Value);
            int code = 0;
            foreach ((string Word, int Code) keyword in keywords)
            {
                if (AsciiCaseInsensitiveComparer.Instance.Equals(word, keyword.Word))
                {
                    code = keyword.Code;
                    break;
                }
            }

            if (code == 0)
            {
                joinType |= JT_ERROR;
                break;
            }

            joinType |= code;
        }

        if ((joinType & (JT_INNER | JT_OUTER)) == (JT_INNER | JT_OUTER) || (joinType & JT_ERROR) != 0 ||
            (joinType & (JT_OUTER | JT_LEFT | JT_RIGHT)) == JT_OUTER)
        {
            string words = Text(a) + (b != null ? " " + Text(b.Value) : "") + (c != null ? " " + Text(c.Value) : "");
            Fail("SCHEMA-22", $"unknown join type: {words}");
        }
    }

    /// <summary>
    /// sqlite3WindowAssemble in window.c
    /// </summary>
    private Window WindowAssemble(Window window, bool partition, bool orderBy, Token? baseName)
    {
        window.HasPartition = partition;
        window.HasOrderBy = orderBy;
        if (baseName != null)
            window.Base = Text(baseName.Value);
        return window;
    }

    /// <summary>
    /// expr ::= VARIABLE in parse.y and sqlite3ExprAssignVarNumber in expr.c: "#N" is a register, only allowed in
    /// SQLite's own nested statements; "?N" must be from ?1 to ?32766; and there may be no more than 32766 parameters
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1317 (sqlite3ExprAssignVarNumber)
    /// </summary>
    private Expr Variable(Token t)
    {
        string z = Text(t);
        if (z[0] == '#' && z.Length > 1 && IsDigit(z[1]))
            Fail("DDL-22", $"near \"{z}\": syntax error");

        int x;
        if (z.Length == 1)
        {
            x = ++_variableCount;
        }
        else if (z[0] == '?')
        {
            if (!long.TryParse(z.Substring(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long i) ||
                i < 1 || i > MaxVariableNumber)
            {
                Fail("DDL-22", $"variable number must be between ?1 and ?{MaxVariableNumber}");
                i = 0;
            }

            x = (int)i;
            if (x > _variableCount)
                _variableCount = x;
        }
        else if (!_variableNames.TryGetValue(z, out x))
        {
            x = ++_variableCount;
            _variableNames[z] = x;
        }

        if (x > MaxVariableNumber)
            Fail("DDL-22", "too many SQL variables");

        return new Expr(TK.VARIABLE) { Text = z };
    }

    /// <summary>
    /// expr ::= expr in_op LP exprlist RP in parse.y: "x IN ()" is FALSE (NOT IN, TRUE) unless x has a function,
    /// "x IN (constant)" is "x = +constant", and "(a, b) IN ((1, 2), ...)" is a VALUES subquery
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/parse.y#L1491 (expr ::= expr in_op LP exprlist RP)
    /// </summary>
    private Expr InList(Expr left, bool not, ExprList? list)
    {
        if (list == null)
        {
            Expr constant = new Expr(TK.STRING) { Text = not ? "true" : "false" };
            IdToTrueFalse(constant);
            return !left.Has(Expr.EP_HasFunc) ? constant : PExpr(not ? TK.OR : TK.AND, constant, left);
        }

        Expr p;
        Expr rhs = list[0].Expr!;
        if (list.Count == 1 && IsConstant(rhs) && left.Op != TK.VECTOR)
        {
            p = PExpr(TK.EQ, left, PExpr(TK.UPLUS, rhs, null));
        }
        else if (list.Count == 1 && rhs.Op == TK.SELECT)
        {
            p = PExpr(TK.IN, left, null);
            AddSelect(p, rhs.Select!);
        }
        else
        {
            p = PExpr(TK.IN, left, null);
            if (left.Op == TK.VECTOR)
            {
                Select values = ExprListToValues(left.List!.Count, list);
                DoubleLinkSelect(values);
                AddSelect(p, values);
            }
            else
            {
                p.List = list;
                SetHeightAndFlags(p);
            }
        }

        return not ? PExpr(TK.NOT, p, null) : p;
    }

    /// <summary>
    /// sqlite3ExprListToValues in expr.c: each row of "(a, b) IN ((1, 2), (3, 4))" must have as many values as the
    /// left side
    /// https://github.com/sqlite/sqlite/blob/version-3.53.4/src/expr.c#L1098 (sqlite3ExprListToValues)
    /// </summary>
    private Select ExprListToValues(int count, ExprList list)
    {
        Select? result = null;
        foreach (ExprListItem item in list)
        {
            Expr p = item.Expr!;
            int n = p.Op == TK.VECTOR ? p.List!.Count : 1;
            if (n != count)
                Fail("SCHEMA-22", $"IN(...) element has {n} term{(n > 1 ? "s" : "")} - expected {count}");

            Select row = NewSelect(p.List, null, null, null, null, null);
            row.IsValues = true;
            if (result != null)
            {
                row.Op = TK.ALL;
                row.Prior = result;
            }

            result = row;
        }

        if (result!.Prior != null)
            result.IsMultiValue = true;
        return result;
    }
}
