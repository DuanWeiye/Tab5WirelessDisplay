namespace WinDisplay.Core;

/// <summary>
/// 闭环找「画面在动时每帧的字节数预算」（PROTOCOL.md「画质」）：在跟得上的前提下尽量大。
/// 「跟得上」是流量控制的说法（<see cref="FlowControl.LastSentKeptUp"/>）：这一帧是等下一拍（限帧率的档）或者等画面源出新画面等来的，
/// 等完的时候在途的还不到平时的帧数。没跟上 = 该发的时候前面的帧还没确认完（Tab5 收不完、解不完），或者画面早就等着了。
/// 调用方每发一帧说一声（<see cref="OnFrame"/>），这里每 <see cref="WindowMs"/> 看一次这一段里「没跟上」的帧占多少：
/// <list type="bullet">
/// <item>占到 <see cref="DownAt"/>：刚升了一步（<see cref="ProbeJudgeMs"/> 之内）就站不住的，马上退回升之前的那个数（它站住过）。
///   别的时候要连着两段都这样才降（链路变差了）：× <see cref="DownScale"/>（第二段占到 <see cref="HardDownAt"/> 就 × <see cref="HardDownScale"/>），三四秒降到位。
///   只有一段的不算——无线上隔一阵就有一次几十毫秒的停顿（实测家里的路由器每 30 秒整停 60 毫秒上下），那之后的几帧都晚，
///   为它降一回、再花五六秒升回来不值得。例外是上面那一条：停顿正落在刚升了一步之后的 <see cref="ProbeJudgeMs"/> 里，一段就退——
///   分不出是这一步升出来的还是碰巧停了一下，退的只是一步（6%）。预算还在往上爬的时候（每 <see cref="UpHoldMs"/> 升一步）这个窗口差不多一直开着，
///   站稳之后才是「一次停顿不降」。降了、退了都记下「在这个数上站不住」（顶），一段时间里不往它那儿升。</item>
/// <item>不到 <see cref="UpAt"/>，离上一次调已经过了 <see cref="UpHoldMs"/>，而且这一段的帧把预算用满了（平均每帧不小于预算的 <see cref="UsedAt"/>）：
///   预算 × <see cref="UpScale"/>，慢慢升回去。</item>
/// <item>中间：不动。</item>
/// </list>
/// 降了之后的 <see cref="SettleMs"/> 不看：在途的那几帧还是按旧的预算发的，Tab5 那头积下的活也要几帧才消得掉，
/// 这一阵「没跟上」说明不了新的预算站不站得住（看了的话每次都会连着降两回）。
/// 开头的 <see cref="SettleMs"/> 也不看：连接刚起来的时候 Tab5 在接管屏幕，头几帧的确认要一百多毫秒才回来（实测），和预算没有关系；
/// 看了的话每条连接一开头就降一回，还要过半分钟才往回试。
/// 为什么要记「顶」：不记的话稳态就是「升到站不住 → 降 → 再升」来回摆，每摆一次画面卡一下。记了之后停在顶下面一点；
/// 顶过了 <see cref="CeilingHoldMs"/> 才作废、再往上试一次（链路可能变好了）。一试又站不住，下一次隔得加倍（最长 <see cref="CeilingHoldMaxMs"/>）；
/// 别的原因站不住的（不是试出来的），间隔回到开头的数。
/// 为什么要看「用满了」：画面简单的时候按原质量压出来也到不了预算，这时跟得上说明不了「预算再大也跟得上」。不看的话预算会一路空涨到上限，
/// 画面一变重，头几帧就按那个没验证过的数发，卡一下再降回来。同一个道理，没用满预算还跟不上的，从「实际每帧多大」降起：要降到它以下才有用。
/// 只看「在动」的帧（大片都在变、按预算降了质量的）：只变了一小块的帧本来就小，说明不了什么。画面停过（隔了一阵没有在动的帧）就重新数。
/// 本类只管算，不碰时钟：时刻由调用方传进来（毫秒，单调时钟）。不是线程安全的。
/// </summary>
public sealed class BudgetControl
{
    /// <summary>多久看一次（毫秒），以及这一段里至少要有几帧在动的才看。</summary>
    public const double WindowMs = 500;
    public const int WindowMinFrames = 8;
    public const double DownAt = 0.25, HardDownAt = 0.6, UpAt = 0.05;
    public const double DownScale = 0.85, HardDownScale = 0.7, UpScale = 1.06;
    public const double UpHoldMs = 2000;
    /// <summary>一段里平均每帧不小于预算的这么多，才算把预算用满了（编码器把在动的帧调在预算的 85%~105%，见 StripEncoder）。</summary>
    public const double UsedAt = 0.8;
    public const double CeilingHoldMs = 30_000, CeilingHoldMaxMs = 240_000;
    /// <summary>降了之后、开头的这么久不看（见类的说明）。</summary>
    public const double SettleMs = 500;
    /// <summary>升了一步之后这么久之内站不住，算这一步没升成。</summary>
    public const double ProbeJudgeMs = 5000;

    private readonly int _min, _max;
    private double _windowStartMs, _lastFrameMs = double.NegativeInfinity;
    private int _frames, _behind;
    private long _bytes;                                        // 这一段里在动的帧一共多少字节
    private double _lastChangeMs = double.NegativeInfinity;     // 上一次调（头一帧也算）是什么时刻
    private int _ceiling = int.MaxValue;                        // 在这个数上站不住过
    private double _ceilingUntilMs = double.NegativeInfinity;   // 到这个时刻之前不往顶那儿升
    private double _ceilingHoldMs = CeilingHoldMs;
    private double _settleUntilMs = double.NegativeInfinity;    // 刚降过：到这个时刻之前的帧不看
    private double _raisedMs = double.NegativeInfinity;         // 上一次升是什么时刻；负无穷 = 那一步已经有了结果（退回去了）
    private int _beforeRaise;                                   // 那一步之前的预算
    private bool _probing;                                      // 那一步越过了作废的顶（是「往上试」）
    private bool _suspect;                                      // 上一段没跟上的占到了 DownAt：下一段还这样就降

    public BudgetControl(int startBytes, int minBytes, int maxBytes)
    {
        if (minBytes <= 0) throw new ArgumentOutOfRangeException(nameof(minBytes), minBytes, "预算的下限要大于 0");
        if (maxBytes < minBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "预算的上限不能比下限小");
        _min = minBytes;
        _max = maxBytes;
        Bytes = Math.Clamp(startBytes, minBytes, maxBytes);
    }

    /// <summary>现在的预算（字节）。</summary>
    public int Bytes { get; private set; }

    /// <summary>刚发出一帧（nowMs）。motion = 这一帧算「在动」；keptUp = 这一帧跟上了（见类的说明）；bytes = 这一帧多大。</summary>
    public void OnFrame(bool motion, bool keptUp, int bytes, double nowMs)
    {
        if (!motion) return;
        if (double.IsNegativeInfinity(_lastChangeMs))               // 头一帧：开头这一阵不看；开头的那个数也要先站够时间再升
        {
            _lastChangeMs = nowMs;
            _settleUntilMs = nowMs + SettleMs;
        }
        if (nowMs < _settleUntilMs) return;
        if (_frames == 0 || nowMs - _lastFrameMs > WindowMs)        // 头一帧，或者画面停过：从这一帧重新数
        {
            if (nowMs - _lastFrameMs > WindowMs) _suspect = false;  // 画面停过：停之前那一段不作数了
            _windowStartMs = nowMs;
            _frames = _behind = 0;
            _bytes = 0;
        }
        _lastFrameMs = nowMs;
        _frames++;
        _bytes += bytes;
        if (!keptUp) _behind++;
        if (nowMs - _windowStartMs < WindowMs || _frames < WindowMinFrames) return;

        double behind = (double)_behind / _frames;
        double used = (double)_bytes / _frames;                     // 这一段里实际每帧多大
        _frames = _behind = 0;
        _bytes = 0;
        if (behind >= DownAt)
        {
            if (nowMs - _raisedMs <= ProbeJudgeMs)
            {
                // 刚升了一步就站不住：退回升之前的数。这一步是往上试的话，下一次隔得久一点再试
                _ceilingHoldMs = _probing ? Math.Min(CeilingHoldMaxMs, _ceilingHoldMs * 2) : CeilingHoldMs;
                _ceiling = Bytes;
                Bytes = _beforeRaise;
            }
            else if (!_suspect)
            {
                _suspect = true;                                    // 头一段：先记着，下一段还这样再降
                return;
            }
            else
            {
                _ceilingHoldMs = CeilingHoldMs;
                int stood = used >= Bytes * UsedAt ? Bytes : (int)used;     // 站不住的是这个数：预算没用满的话是实际的大小
                _ceiling = stood;
                Bytes = Math.Clamp((int)(stood * (behind >= HardDownAt ? HardDownScale : DownScale)), _min, Bytes);
            }
            _suspect = false;
            _raisedMs = double.NegativeInfinity;
            _settleUntilMs = nowMs + SettleMs;
            _ceilingUntilMs = nowMs + _ceilingHoldMs;
            _lastChangeMs = nowMs;
            return;
        }
        _suspect = false;
        if (behind <= UpAt && nowMs - _lastChangeMs >= UpHoldMs && Bytes < _max && used >= Bytes * UsedAt)
        {
            int next = Math.Min(_max, Math.Max(Bytes + 1, (int)(Bytes * UpScale)));
            bool crossing = next >= _ceiling;
            if (crossing && nowMs < _ceilingUntilMs) return;        // 顶还作数：停在它下面
            if (crossing) _ceiling = int.MaxValue;                  // 顶作废了，这一步越过它：站不站得住见上面
            _probing = crossing;
            _beforeRaise = Bytes;
            _raisedMs = nowMs;
            Bytes = next;
            _lastChangeMs = nowMs;
        }
    }
}
