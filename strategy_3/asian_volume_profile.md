//@version=6
// =============================================================================
//  Session Volume Profile (VAH/POC/VAL) + Time Range + Volume + VWAP
//  Everything in ONE indicator, for accounts limited on indicator count.
//
//  v12 CHANGES
//   - Volume is back, drawn inside the PRICE pane (an overlay script cannot
//     own a separate pane). It is mapped into the bottom slice of the price
//     range: baseline = lowest low over the lookback, height = % of range.
//   - Volume moving average, plotted in the same mapped space.
//   - VWAP added, yellow, with its own on/off toggle and anchor choice.
//   - Two EMAs, each independently toggleable with a user-set length.
//
//  KNOWN LIMIT: because the price scale is shared, the volume baseline tracks
//  the lowest low over "Baseline lookback" and will drift when price makes a
//  large move. Raise the lookback or add smoothing to soften it. A perfectly
//  flat baseline is only possible in a separate pane, which needs a second
//  indicator slot.
// =============================================================================
indicator("Session VP + Range + Volume + VWAP + EMA", "VP + Range", overlay = true,
     max_lines_count = 500, max_boxes_count = 500, max_labels_count = 500)

// ------------------------------- INPUTS --------------------------------------
gG = "① General"
tzChartIn = input.string("Asia/Kolkata", "Chart display timezone (IANA)", group = gG,
     tooltip = "Used to resolve fixed clock-time cutoffs. Must match the timezone your chart axis is showing.")
leadBars  = input.int(5, "Lines lead the live candle by (bars)", minval = 0, maxval = 50, group = gG,
     tooltip = "Once a level is marked, its right edge tracks the last candle at this distance until the cutoff time, then freezes.")
keepSess  = input.int(10, "Sessions kept — levels", minval = 1, maxval = 60, group = gG,
     tooltip = "How many past sessions keep their VAH/POC/VAL and Range High/Low lines. Cheap: 7 lines per session against a 500-line budget.")
keepHisto = input.int(3, "Sessions kept — histogram", minval = 1, maxval = 20, group = gG,
     tooltip = "How many past sessions keep their drawn profile histogram. One box per row per session against a 500-box budget, so rows x sessions must stay under ~490. The script clamps this automatically.")

gVP = "② Volume Profile"
vpOn      = input.bool(true, "Enable", group = gVP)
tzVPin    = input.string("America/New_York", "Profile timezone (IANA)", group = gVP)
vpSess    = input.session("0930-1600", "Profile session", group = gVP)
ltfOn     = input.bool(true, "Use intrabar data", group = gVP,
     tooltip = "ON = build the profile from lower-timeframe slices, closer to the built-in Fixed Range VP. OFF = whole chart bars.")
ltfMode   = input.string("Auto", "Intrabar resolution", group = gVP,
     options = ["Auto", "1 min", "3 min", "5 min", "15 min", "10 sec (Premium)", "5 sec (Premium)", "1 sec (Premium)"],
     tooltip = "Seconds options need Premium or higher — picking one on a lower tier throws RE10063. Auto never picks seconds.")
vpCutH    = input.int(11, "Lines stop at — hour", minval = 0, maxval = 23, group = gVP, inline = "vcut")
vpCutM    = input.int(30, "min",                 minval = 0, maxval = 59, group = gVP, inline = "vcut")
bins      = input.int(60, "Rows (price bins)", minval = 5, maxval = 200, group = gVP)
vaPct     = input.float(70.0, "Value area %", minval = 1.0, maxval = 100.0, step = 0.5, group = gVP)
showHisto = input.bool(true, "Draw histogram", group = gVP)
histoW    = input.int(50, "Histogram width (bars)", minval = 5, maxval = 300, group = gVP)
showVPLbl = input.bool(true, "Show VAH/POC/VAL labels", group = gVP)
vpShade   = input.bool(false, "Shade the profile session", group = gVP)
vpShadeCol= input.color(color.new(#1E6A71, 87), "Shade colour", group = gVP)
pocCol    = input.color(#B0B270, "POC",     group = gVP, inline = "vc1")
vaCol     = input.color(#BDBDBD, "VAH/VAL", group = gVP, inline = "vc1")
vaFill    = input.color(color.new(#B7BAB9, 25), "VA bins",    group = gVP, inline = "vc2")
nvaFill   = input.color(color.new(#B7BAB9, 68), "Other bins", group = gVP, inline = "vc2")

gR = "③ Time Range"
rOn       = input.bool(true, "Enable", group = gR)
tzRin     = input.string("Asia/Kolkata", "Range timezone (IANA)", group = gR)
rSess     = input.session("0330-0530", "Range session", group = gR)
rCutH     = input.int(11, "Lines stop at — hour", minval = 0, maxval = 23, group = gR, inline = "rcut")
rCutM     = input.int(30, "min",                 minval = 0, maxval = 59, group = gR, inline = "rcut")
rFullVert = input.bool(true, "Verticals span full pane height", group = gR)
rvCol     = input.color(#BDBDBD, "Verticals", group = gR, inline = "rc1")
rhCol     = input.color(#1E6A71, "Range H/L", group = gR, inline = "rc1")
showRLbl  = input.bool(true, "Show Range H/L labels", group = gR)

gV = "④ Volume"
volOn     = input.bool(true, "Show volume", group = gV)
volHeight = input.float(15.0, "Height (% of price range)", minval = 2.0, maxval = 50.0, step = 1.0, group = gV,
     tooltip = "How much of the visible price range the tallest column fills.")
volLook   = input.int(1000, "Baseline lookback (bars)", minval = 100, maxval = 5000, group = gV,
     tooltip = "The floor sits at the lowest low over this many bars. Longer = flatter and more stable, but slower to follow a trending market.")
volSmooth = input.int(0, "Baseline smoothing (bars)", minval = 0, maxval = 500, group = gV,
     tooltip = "0 = off. Above 1, the floor is averaged so it slopes gently instead of stepping when the lookback low changes.")
volUpCol  = input.color(color.new(#26A69A, 45), "Up",   group = gV, inline = "vv")
volDnCol  = input.color(color.new(#EF5350, 45), "Down", group = gV, inline = "vv")
volMaOn   = input.bool(true, "Moving average", group = gV, inline = "vma")
volMaLen  = input.int(20, "len", minval = 1, maxval = 500, group = gV, inline = "vma")
volMaCol  = input.color(#B0B270, "", group = gV, inline = "vma")

gW = "⑤ VWAP"
vwapOn    = input.bool(true, "Show VWAP", group = gW)
vwapMode  = input.string("Session (daily)", "Anchor", options = ["Session (daily)", "Profile session", "Week", "Month"], group = gW,
     tooltip = "Where the VWAP calculation resets. 'Profile session' anchors it to the profile session open set in group ②.")
vwapSrc   = input.source(hlc3, "Source", group = gW)
vwapCol   = input.color(#FFEB3B, "Colour", group = gW, inline = "wc")
vwapWidth = input.int(1, "Width", minval = 1, maxval = 4, group = gW, inline = "wc")

gE = "⑥ Moving Averages"
ema1On   = input.bool(true,  "EMA 1", group = gE, inline = "e1")
ema1Len  = input.int(9,      "len", minval = 1, maxval = 1000, group = gE, inline = "e1")
ema1Col  = input.color(#4FC3F7, "", group = gE, inline = "e1")
ema2On   = input.bool(true,  "EMA 2", group = gE, inline = "e2")
ema2Len  = input.int(100,    "len", minval = 1, maxval = 1000, group = gE, inline = "e2")
ema2Col  = input.color(#BA68C8, "", group = gE, inline = "e2")
emaSrc   = input.source(close, "Source", group = gE)
emaWidth = input.int(1, "Width", minval = 1, maxval = 4, group = gE)

tzChart = tzChartIn == "Exchange" ? syminfo.timezone : tzChartIn
tzVP    = tzVPin    == "Exchange" ? syminfo.timezone : tzVPin
tzR     = tzRin     == "Exchange" ? syminfo.timezone : tzRin

bool tfOk    = timeframe.isintraday
int  chartSec= timeframe.in_seconds(timeframe.period)
int  barMs   = chartSec * 1000
int  leadMs  = leadBars * barMs

// --------------------------- INTRABAR DATA -----------------------------------
// "Auto" never selects a seconds-based resolution, so it is safe on every plan.
string ltfRes = switch ltfMode
    "1 min"            => "1"
    "3 min"            => "3"
    "5 min"            => "5"
    "15 min"           => "15"
    "10 sec (Premium)" => "10S"
    "5 sec (Premium)"  => "5S"
    "1 sec (Premium)"  => "1S"
    => chartSec >= 900 ? "3" : "1"

bool ltfValid = ltfOn and timeframe.in_seconds(ltfRes) < chartSec
[ltfH, ltfL, ltfV] = request.security_lower_tf(syminfo.tickerid,
     ltfValid ? ltfRes : timeframe.period, [high, low, volume])

// --------------------------- SESSION DETECTION -------------------------------
// Computed early because the VWAP anchor option depends on vpNew.
inVP  = vpOn and tfOk and not na(time(timeframe.period, vpSess, tzVP))
vpNew = inVP and not inVP[1]
vpEnd = not inVP and inVP[1]

inR  = rOn and tfOk and not na(time(timeframe.period, rSess, tzR))
rNew = inR and not inR[1]

// ------------------------------- VWAP ----------------------------------------
bool vwapAnchor = switch vwapMode
    "Profile session" => vpNew
    "Week"            => timeframe.change("1W")
    "Month"           => timeframe.change("1M")
    => timeframe.change("1D")

float vwapVal = ta.vwap(vwapSrc, vwapAnchor)
plot(vwapOn ? vwapVal : na, "VWAP", color = vwapCol, linewidth = vwapWidth)

// ---------------------------- MOVING AVERAGES --------------------------------
float ema1 = ta.ema(emaSrc, ema1Len)
float ema2 = ta.ema(emaSrc, ema2Len)
plot(ema1On ? ema1 : na, "EMA 1", color = ema1Col, linewidth = emaWidth)
plot(ema2On ? ema2 : na, "EMA 2", color = ema2Col, linewidth = emaWidth)

// ------------------------------ VOLUME ---------------------------------------
// Mapped into the bottom slice of the price range. Because it shares the price
// scale, the floor follows the lookback low — that is the cost of keeping this
// in a single indicator rather than a dedicated pane.
float volFloorRaw = ta.lowest(low, volLook)
float volFloor    = volSmooth > 1 ? ta.sma(volFloorRaw, volSmooth) : volFloorRaw
float volCeil     = ta.highest(high, volLook)
float volMaxV     = ta.highest(nz(volume), volLook)
float volK        = volMaxV > 0 ? (volCeil - volFloor) * (volHeight / 100.0) / volMaxV : 0.0

float volTop  = volFloor + nz(volume) * volK
float volMaV  = ta.sma(nz(volume), volMaLen)
float volMaY  = volFloor + volMaV * volK

plotcandle(volOn ? volFloor : na, volOn ? volTop : na, volOn ? volFloor : na, volOn ? volTop : na,
     "Volume",
     color       = close >= open ? volUpCol : volDnCol,
     bordercolor = close >= open ? volUpCol : volDnCol,
     wickcolor   = color.new(color.black, 100))
plot(volOn and volMaOn ? volMaY : na, "Volume MA", color = volMaCol, linewidth = 1)

// Next occurrence of hh:mm (in tz) strictly after anchorT.
f_nextClock(anchorT, tz, hh, mm) =>
    int y  = year(anchorT, tz)
    int mo = month(anchorT, tz)
    int d  = dayofmonth(anchorT, tz)
    int c  = timestamp(tz, y, mo, d, hh, mm, 0)
    c <= anchorT ? timestamp(tz, y, mo, d + 1, hh, mm, 0) : c

// ------------------------- PROFILE CALCULATION -------------------------------
f_profile(hs, ls, vs, nBins, vaP) =>
    int n = array.size(hs)
    float top = n > 0 ? array.max(hs) : na
    float bot = n > 0 ? array.min(ls) : na
    float rng = top - bot
    float bs  = rng / nBins
    binVol = array.new<float>(nBins, 0.0)
    float poc = na, float vah = na, float val = na
    if n > 0 and rng > 0
        for i = 0 to n - 1
            float h = array.get(hs, i)
            float l = array.get(ls, i)
            float v = array.get(vs, i)
            float br = h - l
            if br <= 0
                int idx = math.max(0, math.min(nBins - 1, int(math.floor((h - bot) / bs))))
                array.set(binVol, idx, array.get(binVol, idx) + v)
            else
                int i0 = math.max(0, math.min(nBins - 1, int(math.floor((l - bot) / bs))))
                int i1 = math.max(0, math.min(nBins - 1, int(math.floor((h - bot) / bs))))
                for b = i0 to i1
                    float bl = bot + b * bs
                    float bh = bl + bs
                    float ov = math.min(h, bh) - math.max(l, bl)
                    if ov > 0
                        array.set(binVol, b, array.get(binVol, b) + v * ov / br)
        float totalV = array.sum(binVol)
        if totalV > 0
            int   pocIdx = 0
            float maxV   = -1.0
            for b = 0 to nBins - 1
                float cv = array.get(binVol, b)
                if cv > maxV
                    maxV   := cv
                    pocIdx := b
            float target = totalV * vaP / 100.0
            float acc = maxV
            int up = pocIdx
            int dn = pocIdx
            for _i = 0 to nBins - 1
                if acc < target
                    float vUp = up < nBins - 1 ? array.get(binVol, up + 1) : -1.0
                    float vDn = dn > 0         ? array.get(binVol, dn - 1) : -1.0
                    if vUp < 0 and vDn < 0
                        acc := target
                    else if vUp >= vDn
                        up  := up + 1
                        acc := acc + vUp
                    else
                        dn  := dn - 1
                        acc := acc + vDn
            poc := bot + (pocIdx + 0.5) * bs
            vah := bot + (up + 1) * bs
            val := bot + dn * bs
    [poc, vah, val, bot, bs, binVol]

// ------------------------------ STATE ----------------------------------------
var array<float> hA = array.new<float>()
var array<float> lA = array.new<float>()
var array<float> vA = array.new<float>()
var int  vpStartT = na
var int  vpStopT  = na
var bool vpFrozen = true

var line  lPoc = na, var line  lVah = na, var line  lVal = na
var label bPoc = na, var label bVah = na, var label bVal = na
var box   vpBox = na

var array<line>  vpLineStore = array.new<line>()
var array<label> vpLblStore  = array.new<label>()
var array<box>   vpBoxStore  = array.new<box>()
var array<box>   boxArchive  = array.new<box>()
var array<box>   boxCurrent  = array.new<box>()

int maxVPLines = keepSess * 3
// Boxes are the scarce resource: 500 total, minus the session-shading boxes.
int maxBoxes   = math.min(keepHisto * bins, 490 - (vpShade ? keepSess : 0))

var float rH = na
var float rL = na
var int   rStartT = na
var int   rStopT  = na
var bool  rFrozen = true
var line  lV1 = na, var line lV2 = na, var line lRH = na, var line lRL = na
var label bRH = na, var label bRL = na

// --------------------------- NEW PROFILE SESSION -----------------------------
if vpNew
    array.clear(hA), array.clear(lA), array.clear(vA)
    vpStartT := time
    vpStopT  := f_nextClock(time, tzChart, vpCutH, vpCutM)
    vpFrozen := false

    if array.size(boxCurrent) > 0
        for i = 0 to array.size(boxCurrent) - 1
            array.push(boxArchive, array.get(boxCurrent, i))
        array.clear(boxCurrent)
    if array.size(boxArchive) > maxBoxes
        for i = 1 to array.size(boxArchive) - maxBoxes
            box.delete(array.shift(boxArchive))

    lPoc := line.new(vpStartT, close, vpStartT, close, xloc = xloc.bar_time, color = pocCol, style = line.style_solid, width = 1)
    lVah := line.new(vpStartT, close, vpStartT, close, xloc = xloc.bar_time, color = vaCol,  style = line.style_solid, width = 1)
    lVal := line.new(vpStartT, close, vpStartT, close, xloc = xloc.bar_time, color = vaCol,  style = line.style_solid, width = 1)
    array.push(vpLineStore, lPoc), array.push(vpLineStore, lVah), array.push(vpLineStore, lVal)
    if array.size(vpLineStore) > maxVPLines
        for i = 1 to array.size(vpLineStore) - maxVPLines
            line.delete(array.shift(vpLineStore))

    if vpShade
        vpBox := box.new(vpStartT, high, time, low, xloc = xloc.bar_time, bgcolor = vpShadeCol, border_color = color.new(color.black, 100))
        array.push(vpBoxStore, vpBox)
        if array.size(vpBoxStore) > keepSess
            for i = 1 to array.size(vpBoxStore) - keepSess
                box.delete(array.shift(vpBoxStore))

    if showVPLbl
        bPoc := label.new(vpStartT, close, "POC", xloc = xloc.bar_time, style = label.style_label_left, color = color.new(color.black, 100), textcolor = pocCol, size = size.small)
        bVah := label.new(vpStartT, close, "VAH", xloc = xloc.bar_time, style = label.style_label_left, color = color.new(color.black, 100), textcolor = vaCol,  size = size.small)
        bVal := label.new(vpStartT, close, "VAL", xloc = xloc.bar_time, style = label.style_label_left, color = color.new(color.black, 100), textcolor = vaCol,  size = size.small)
        array.push(vpLblStore, bPoc), array.push(vpLblStore, bVah), array.push(vpLblStore, bVal)
        if array.size(vpLblStore) > maxVPLines
            for i = 1 to array.size(vpLblStore) - maxVPLines
                label.delete(array.shift(vpLblStore))

// ---------------------- COLLECT BARS (intrabar aware) ------------------------
if inVP
    int nIntra = array.size(ltfH)
    if ltfValid and nIntra > 0
        for i = 0 to nIntra - 1
            array.push(hA, array.get(ltfH, i))
            array.push(lA, array.get(ltfL, i))
            array.push(vA, nz(array.get(ltfV, i)))
    else
        array.push(hA, high)
        array.push(lA, low)
        array.push(vA, nz(volume))
    if vpShade and not na(vpBox)
        box.set_rightbottom(vpBox, time, math.min(box.get_bottom(vpBox), low))
        box.set_lefttop(vpBox, vpStartT, math.max(box.get_top(vpBox), high))

// --------------------------- BUILD / UPDATE PROFILE --------------------------
bool doCalc = (vpEnd or (inVP and barstate.islast)) and array.size(hA) > 0

if doCalc
    [poc, vah, val, bot, bs, bv] = f_profile(hA, lA, vA, bins, vaPct)
    if not na(poc)
        line.set_xy1(lPoc, vpStartT, poc), line.set_y2(lPoc, poc)
        line.set_xy1(lVah, vpStartT, vah), line.set_y2(lVah, vah)
        line.set_xy1(lVal, vpStartT, val), line.set_y2(lVal, val)
        if showVPLbl
            label.set_y(bPoc, poc), label.set_text(bPoc, "POC " + str.tostring(poc, format.mintick))
            label.set_y(bVah, vah), label.set_text(bVah, "VAH " + str.tostring(vah, format.mintick))
            label.set_y(bVal, val), label.set_text(bVal, "VAL " + str.tostring(val, format.mintick))

        if showHisto
            if array.size(boxCurrent) > 0
                for i = 0 to array.size(boxCurrent) - 1
                    box.delete(array.get(boxCurrent, i))
                array.clear(boxCurrent)
            float mx = array.max(bv)
            if mx > 0
                for b = 0 to bins - 1
                    float v = array.get(bv, b)
                    if v > 0
                        int   w   = math.max(barMs, int(math.round(histoW * barMs * v / mx)))
                        float bl  = bot + b * bs
                        float bh  = bl + bs
                        float mid = bl + bs * 0.5
                        bool isPoc = math.abs(mid - poc) <= bs * 0.5
                        bool isVA  = mid >= val and mid <= vah
                        color c = isPoc ? color.new(pocCol, 45) : isVA ? vaFill : nvaFill
                        array.push(boxCurrent, box.new(vpStartT, bh, vpStartT + w, bl,
                             xloc = xloc.bar_time, bgcolor = c, border_color = color.new(#B7BAB9, 90), border_width = 1))

// ============================== TIME RANGE ===================================
var array<line>  rLineStore = array.new<line>()
var array<label> rLblStore  = array.new<label>()

vExt = rFullVert ? extend.both : extend.none

if rNew
    rH := high
    rL := low
    rStartT := time
    rStopT  := f_nextClock(time, tzChart, rCutH, rCutM)
    rFrozen := false
    float y1 = low  - syminfo.mintick
    float y2 = high + syminfo.mintick
    lV1 := line.new(rStartT, y1, rStartT, y2, xloc = xloc.bar_time, extend = vExt, color = rvCol, style = line.style_solid, width = 1)
    lV2 := line.new(rStartT, y1, rStartT, y2, xloc = xloc.bar_time, extend = vExt, color = rvCol, style = line.style_solid, width = 1)
    lRH := line.new(rStartT, high, rStartT, high, xloc = xloc.bar_time, color = rhCol, style = line.style_solid, width = 1)
    lRL := line.new(rStartT, low,  rStartT, low,  xloc = xloc.bar_time, color = rhCol, style = line.style_solid, width = 1)
    array.push(rLineStore, lV1), array.push(rLineStore, lV2), array.push(rLineStore, lRH), array.push(rLineStore, lRL)
    if array.size(rLineStore) > keepSess * 4
        for i = 1 to array.size(rLineStore) - keepSess * 4
            line.delete(array.shift(rLineStore))

    if showRLbl
        bRH := label.new(rStartT, high, "Range High", xloc = xloc.bar_time, style = label.style_label_left, color = color.new(color.black, 100), textcolor = rhCol, size = size.small)
        bRL := label.new(rStartT, low,  "Range Low",  xloc = xloc.bar_time, style = label.style_label_left, color = color.new(color.black, 100), textcolor = rhCol, size = size.small)
        array.push(rLblStore, bRH), array.push(rLblStore, bRL)
        if array.size(rLblStore) > keepSess * 2
            for i = 1 to array.size(rLblStore) - keepSess * 2
                label.delete(array.shift(rLblStore))

if inR and not rNew
    rH := math.max(rH, high)
    rL := math.min(rL, low)
    float y1 = rL - syminfo.mintick
    float y2 = rH + syminfo.mintick
    line.set_xy1(lV1, rStartT, y1), line.set_xy2(lV1, rStartT, y2)
    line.set_xy1(lV2, time,    y1), line.set_xy2(lV2, time,    y2)
    line.set_y1(lRH, rH), line.set_y2(lRH, rH)
    line.set_y1(lRL, rL), line.set_y2(lRL, rL)
    if showRLbl
        label.set_y(bRH, rH), label.set_text(bRH, "Range High " + str.tostring(rH, format.mintick))
        label.set_y(bRL, rL), label.set_text(bRL, "Range Low "  + str.tostring(rL, format.mintick))

// ================= LIVE TRACKING OF THE FIVE LEVEL LINES =====================
// Right edge follows the last candle, leading it by leadBars, until the cutoff.
if not vpFrozen and not na(lPoc)
    int x = math.min(vpStopT, time + leadMs)
    line.set_x2(lPoc, x), line.set_x2(lVah, x), line.set_x2(lVal, x)
    if showVPLbl
        label.set_x(bPoc, x), label.set_x(bVah, x), label.set_x(bVal, x)
    if time >= vpStopT
        vpFrozen := true

if not rFrozen and not na(lRH)
    int x = math.min(rStopT, time + leadMs)
    line.set_x2(lRH, x), line.set_x2(lRL, x)
    if showRLbl
        label.set_x(bRH, x), label.set_x(bRL, x)
    if time >= rStopT
        rFrozen := true

// ------------------------------- WARNINGS ------------------------------------
var label warnLbl = na
if barstate.islast
    label.delete(warnLbl)
    warnLbl := not tfOk ? label.new(bar_index, close, "Switch to an intraday timeframe", style = label.style_label_left, color = color.new(color.red, 20), textcolor = color.white, size = size.small) : na