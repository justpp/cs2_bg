using Windows.Media.Control;

namespace Cs2VoiceMusic.Media;

/// <summary>当前媒体会话信息。</summary>
public sealed record MediaInfo(string Title, string Artist, string Album, bool IsPlaying, string SourceApp);

/// <summary>
/// 通过 Windows 系统媒体传输控制（SMTC）操作 QQ 音乐。
///
/// 优点：走系统标准接口，不需要模拟按键、不受窗口焦点影响，
/// 也不会被反作弊误判为输入注入。
/// </summary>
public sealed class SystemMediaController : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly object _sync = new();

    public bool IsAvailable { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>确保已取得管理器与会话。</summary>
    public bool EnsureSession(bool preferQqMusic = true)
    {
        lock (_sync)
        {
            try
            {
                _manager ??= GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                if (_manager is null)
                {
                    IsAvailable = false;
                    LastError = "无法取得系统媒体会话管理器";
                    return false;
                }

                var sessions = _manager.GetSessions();

                if (preferQqMusic)
                {
                    // 优先选 QQ 音乐
                    var qq = sessions.FirstOrDefault(s =>
                        s.SourceAppUserModelId.Contains("QQMusic", StringComparison.OrdinalIgnoreCase));
                    if (qq is not null)
                    {
                        _session = qq;
                        IsAvailable = true;
                        LastError = null;
                        return true;
                    }
                }

                // 回退：当前会话 → 任意会话。
                // 注意：会话对象会随 QQ 音乐退出而失效，失效时下面各方法会把
                // _session 置空并重新枚举，避免一直用一个死会话。
                _session = _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
                IsAvailable = _session is not null;
                if (!IsAvailable) LastError = "未找到可控制的媒体会话（QQ 音乐可能未启动或未在播放）";
                else LastError = null;
                return IsAvailable;
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>读取当前播放信息。</summary>
    public MediaInfo? GetInfo()
    {
        lock (_sync)
        {
            if (_session is null && !EnsureSession()) return null;

            try
            {
                var props = _session!.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
                var playback = _session.GetPlaybackInfo();
                return new MediaInfo(
                    props?.Title ?? "",
                    props?.Artist ?? "",
                    props?.AlbumTitle ?? "",
                    playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    _session.SourceAppUserModelId);
            }
            catch (Exception ex)
            {
                // 会话很可能已经失效（QQ 音乐退出/重启），下一次调用重新枚举
                _session = null;
                IsAvailable = false;
                LastError = ex.Message;
                return null;
            }
        }
    }

    public bool PlayPause()
    {
        lock (_sync)
        {
            try
            {
                if (_session is null && !EnsureSession()) return false;

                var status = _session!.GetPlaybackInfo().PlaybackStatus;
                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return _session.TryPauseAsync().AsTask().GetAwaiter().GetResult();
                }
                return _session.TryPlayAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _session = null;
                LastError = ex.Message;
                return false;
            }
        }
    }

    public bool Next()
    {
        lock (_sync)
        {
            try
            {
                if (_session is null && !EnsureSession()) return false;
                return _session!.TrySkipNextAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) { _session = null; LastError = ex.Message; return false; }
        }
    }

    public bool Previous()
    {
        lock (_sync)
        {
            try
            {
                if (_session is null && !EnsureSession()) return false;
                return _session!.TrySkipPreviousAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) { _session = null; LastError = ex.Message; return false; }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _session = null;
            _manager = null;
        }
    }
}
