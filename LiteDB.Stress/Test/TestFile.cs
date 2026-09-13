using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace LiteDB.Stress
{
    public class TestFile
    {
        public TimeSpan Timeout { get; }
        public string Filename { get; }
        public string Output { get; }
        public bool Delete { get; }
        public List<string> Setup { get; }
        public List<ITestItem> Tasks { get; }

        public TestFile(string filename)
        {
            var doc = new XmlDocument();
            doc.Load(filename);

            var root = doc.DocumentElement;
            var children = root.SelectNodes("*");

            this.Timeout = TimeSpanEx.Parse(root.GetAttribute("timeout"));
            this.Filename = root.GetAttribute("filename");
            this.Delete = bool.Parse(root.GetAttribute("delete"));
            this.Output = Path.Combine(Path.GetDirectoryName(this.Filename), Path.GetFileNameWithoutExtension(this.Filename) + ".log");

            this.Setup = new List<string>();
            this.Tasks = new List<ITestItem>();

            foreach(XmlElement el in children)
            {
                if (el.Name == "setup")
                {
                    this.Setup.Add(el.InnerText);
                }
                else
                {
                    var item = el.Name == "insert" ?
                        (ITestItem)new InsertTaskItem(el) :
                        (ITestItem)new SqlTaskItem(el);

                    this.Tasks.Add(item);
                }
            }
        }
    }
}
