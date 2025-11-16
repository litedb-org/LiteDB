using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represents the complete structure of a query including filtering, ordering, grouping, and projection options.
    /// </summary>
    /// <remarks>
    /// This class is typically built using the fluent interface provided by <see cref="ILiteQueryable{T}"/> methods.
    /// It contains all query components that will be executed against a LiteDB collection.
    /// </remarks>
    public partial class Query
    {
        /// <summary>
        /// Gets or sets the SELECT expression that defines the projection of query results. Default is <see cref="BsonExpression.Root"/> (all fields).
        /// </summary>
        public BsonExpression Select { get; set; } = BsonExpression.Root;

        /// <summary>
        /// Gets the list of INCLUDE expressions for loading related documents via DbRef.
        /// </summary>
        public List<BsonExpression> Includes { get; } = new List<BsonExpression>();

        /// <summary>
        /// Gets the list of WHERE expressions that filter documents. Multiple expressions are combined with AND logic.
        /// </summary>
        public List<BsonExpression> Where { get; } = new List<BsonExpression>();

        /// <summary>
        /// Gets the list of ORDER BY expressions that define the sort order of results.
        /// </summary>
        public List<QueryOrder> OrderBy { get; } = new List<QueryOrder>();

        /// <summary>
        /// Gets or sets the GROUP BY expression for grouping results. Default is <see langword="null"/> (no grouping).
        /// </summary>
        public BsonExpression GroupBy { get; set; } = null;

        /// <summary>
        /// Gets or sets the HAVING expression that filters grouped results. Default is <see langword="null"/> (no filter).
        /// </summary>
        public BsonExpression Having { get; set; } = null;

        /// <summary>
        /// Gets or sets the number of documents to skip in the result set. Default is 0.
        /// </summary>
        public int Offset { get; set; } = 0;

        /// <summary>
        /// Gets or sets the maximum number of documents to return. Default is <see cref="int.MaxValue"/> (no limit).
        /// </summary>
        public int Limit { get; set; } = int.MaxValue;

        /// <summary>
        /// Gets or sets whether to execute the query with a write lock for update operations. Default is <see langword="false"/>.
        /// </summary>
        public bool ForUpdate { get; set; } = false;

        /// <summary>
        /// Gets or sets the field name for vector similarity search. Default is <see langword="null"/>.
        /// </summary>
        public string VectorField { get; set; } = null;

        /// <summary>
        /// Gets or sets the target vector for similarity search. Default is <see langword="null"/>.
        /// </summary>
        public float[] VectorTarget { get; set; } = null;

        /// <summary>
        /// Gets or sets the maximum distance threshold for vector similarity search. Default is <see cref="double.MaxValue"/> (no limit).
        /// </summary>
        public double VectorMaxDistance { get; set; } = double.MaxValue;

        /// <summary>
        /// Gets whether this query has a vector similarity filter configured.
        /// </summary>
        public bool HasVectorFilter => VectorField != null && VectorTarget != null;

        /// <summary>
        /// Gets or sets the name of the collection where query results will be inserted (SELECT INTO). Default is <see langword="null"/>.
        /// </summary>
        public string Into { get; set; }

        /// <summary>
        /// Gets or sets the auto-ID generation strategy for the INTO collection. Default is <see cref="BsonAutoId.ObjectId"/>.
        /// </summary>
        public BsonAutoId IntoAutoId { get; set; } = BsonAutoId.ObjectId;

        /// <summary>
        /// Gets or sets whether to return the query execution plan instead of executing the query. Default is <see langword="false"/>.
        /// </summary>
        public bool ExplainPlan { get; set; }

        /// <summary>
        /// Converts this query structure to a SQL-like string representation.
        /// </summary>
        /// <param name="collection">The collection name to include in the SQL statement.</param>
        /// <returns>A SQL-like string representing the complete query structure.</returns>
        /// <remarks>
        /// <para>The SQL format follows this structure:</para>
        /// <code>
        /// [ EXPLAIN ]
        ///    SELECT {selectExpr}
        ///    [ INTO {newcollection|$function} [ : {autoId} ] ]
        ///    [ FROM {collection|$function} ]
        /// [ INCLUDE {pathExpr0} [, {pathExprN} ]
        ///   [ WHERE {filterExpr} ]
        ///   [ GROUP BY {groupByExpr} ]
        ///  [ HAVING {filterExpr} ]
        ///   [ ORDER BY {orderByExpr} [ ASC | DESC ] ]
        ///   [ LIMIT {number} ]
        ///  [ OFFSET {number} ]
        ///     [ FOR UPDATE ]
        /// </code>
        /// </remarks>
        public string ToSQL(string collection)
        {
            var sb = new StringBuilder();

            if (this.ExplainPlan)
            {
                sb.AppendLine("EXPLAIN");
            }

            sb.AppendLine($"SELECT {this.Select.Source}");

            if (this.Into != null)
            {
                sb.AppendLine($"INTO {this.Into}:{IntoAutoId.ToString().ToLower()}");
            }

            sb.AppendLine($"FROM {collection}");

            if (this.Includes.Count > 0)
            {
                sb.AppendLine($"INCLUDE {string.Join(", ", this.Includes.Select(x => x.Source))}");
            }

            

            if (this.GroupBy != null)
            {
                sb.AppendLine($"GROUP BY {this.GroupBy.Source}");
            }

            if (this.Having != null)
            {
                sb.AppendLine($"HAVING {this.Having.Source}");
            }

            if (this.OrderBy.Count > 0)
            {
                var orderBy = this.OrderBy
                    .Select(x => $"{x.Expression.Source} {(x.Order == Query.Ascending ? "ASC" : "DESC")}");

                sb.AppendLine($"ORDER BY {string.Join(", ", orderBy)}");
            }

            if (this.Limit != int.MaxValue)
            {
                sb.AppendLine($"LIMIT {this.Limit}");
            }

            if (this.Offset != 0)
            {
                sb.AppendLine($"OFFSET {this.Offset}");
            }

            if (this.ForUpdate)
            {
                sb.AppendLine($"FOR UPDATE");
            }

            if (this.HasVectorFilter)
            {
                var field = this.VectorField;

                if (!string.IsNullOrEmpty(field))
                {
                    field = field.Trim();

                    if (!field.StartsWith("$", StringComparison.Ordinal))
                    {
                        field = field.StartsWith(".", StringComparison.Ordinal)
                            ? "$" + field
                            : "$." + field;
                    }
                }

                var vectorExpr = $"VECTOR_SIM({field}, [{string.Join(",", this.VectorTarget)}])";
                if (this.Where.Count > 0)
                {
                    sb.AppendLine($"WHERE ({string.Join(" AND ", this.Where.Select(x => x.Source))}) AND {vectorExpr} <= {this.VectorMaxDistance}");
                }
                else
                {
                    sb.AppendLine($"WHERE {vectorExpr} <= {this.VectorMaxDistance}");
                }
            }
            else if (this.Where.Count > 0)
            {
                sb.AppendLine($"WHERE {string.Join(" AND ", this.Where.Select(x => x.Source))}");
            }

            return sb.ToString().Trim();
        }
    }
}
