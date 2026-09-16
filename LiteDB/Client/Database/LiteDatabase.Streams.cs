namespace LiteDB
{
    public partial class LiteDatabase
    {
        private StreamReferenceMapper _streamReferenceMapper;

        private StreamReferenceMapper GetStreamReferenceMapper()
        {
            return _streamReferenceMapper ??= new StreamReferenceMapper(() => this.FileStorage);
        }
    }
}
