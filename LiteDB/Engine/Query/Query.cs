using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represent full query options
    /// </summary>
    public partial class Query
    {
        public BsonExpression Select { get; set; } = BsonExpression.Root;

        public List<BsonExpression> Includes { get; } = new List<BsonExpression>();
        public List<BsonExpression> Where { get; } = new List<BsonExpression>();

        public BsonExpression OrderBy { get; set; } = null;
        public int Order { get; set; } = Query.Ascending;

        public BsonExpression GroupBy { get; set; } = null;
        public BsonExpression Having { get; set; } = null;

        public int Offset { get; set; } = 0;
        public int Limit { get; set; } = int.MaxValue;
        public bool ForUpdate { get; set; } = false;

        public string VectorField { get; set; } = null;
        public float[] VectorTarget { get; set; } = null;
        public double VectorMaxDistance { get; set; } = double.MaxValue;
        public bool HasVectorFilter => VectorField != null && VectorTarget != null;

        public string Into { get; set; }
        public BsonAutoId IntoAutoId { get; set; } = BsonAutoId.ObjectId;

        public bool ExplainPlan { get; set; }

        /// <summary>
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
        /// </summary>
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

            if (this.OrderBy != null)
            {
                sb.AppendLine($"ORDER BY {this.OrderBy.Source} {(this.Order == Query.Ascending ? "ASC" : "DESC")}");
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
                var vectorExpr = $"VECTOR_SIM($.{this.VectorField}, [{string.Join(",", this.VectorTarget)}])";
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
