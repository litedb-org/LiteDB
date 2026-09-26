#if NET8_0_OR_GREATER
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private SharedWriterResume _writerResume;
        private SharedCoordinationStatus _writerResumeStatus;

        private void PrepareWriterResume(bool abandoned)
        {
            var saved = _writerResume;
            var fence = _writerResumeStatus;
            _writerResume = null;
            _settings.WriterResume = null;
            _settings.WriterResumeValid = null;
            _settings.CaptureWriterResume = false;
            if (_coordination == null || !_coordination.TryRead(out var current)) return;
            _settings.CaptureWriterResume = true;
            if (abandoned || saved == null || !current.SameWriterStorage(fence)) return;
            _settings.WriterResume = saved;
            _settings.WriterResumeValid = () => _coordination.CanResume(fence);
        }
    }
}
#endif
