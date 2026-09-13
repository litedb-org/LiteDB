using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace LiteDB.Stress
{
    public class SqlTaskItem : ITestItem
    {
        public string Name { get; }
        public int TaskCount { get; }
        public TimeSpan Sleep { get; }
        public string Sql { get; }

        public SqlTaskItem(XmlElement el)
        {
            this.Name = string.IsNullOrEmpty(el.GetAttribute("name")) ? el.InnerText.Split(' ').First() : el.GetAttribute("name");
            this.TaskCount = string.IsNullOrEmpty(el.GetAttribute("tasks")) ? 1 : int.Parse(el.GetAttribute("tasks"));
            this.Sleep = TimeSpanEx.Parse(el.GetAttribute("sleep"));
            this.Sql = el.InnerText;
        }

        public BsonValue Execute(LiteDatabase db)
        {
            using (var reader = db.Execute(this.Sql))
            {
                return reader.FirstOrDefault();
            }
        }
    }
}
