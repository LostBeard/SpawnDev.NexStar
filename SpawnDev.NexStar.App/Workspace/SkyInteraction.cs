using System.Numerics;
using SpawnDev.GameUI;
using SpawnDev.GameUI.Input;
using SpawnDev.NexStar.Sky;

namespace SpawnDev.NexStar.App.Workspace;

/// <summary>
/// Moves the chart with the pointer: drag to pan, wheel or pinch to zoom, and a short tap that did not drag to pick. Anything over the HUD is the HUD's,
/// not the chart's. One class for mouse and touch so the phone and the desktop behave the same.
/// </summary>
public sealed class SkyInteraction
{
    private readonly SkyView _view;
    private readonly UIElement _hudRoot;

    private PointerType? _dragType;
    private Vector2 _pressPosition;
    private bool _moved;
    private double _lastPinch;
    private bool _pinching;

    /// <summary>Raised for a press and release that did not move: the screen position of the tap.</summary>
    public event Action<Vector2>? Tapped;

    /// <summary>True while the user is dragging or pinching (the chart should not auto-follow anything).</summary>
    public bool Active => _dragType != null || _pinching;

    public SkyInteraction(SkyView view, UIElement hudRoot)
    {
        _view = view;
        _hudRoot = hudRoot;
    }

    private bool OverHud(Vector2 position)
    {
        var hit = _hudRoot.HitTest(position);
        return hit != null && !ReferenceEquals(hit, _hudRoot);
    }

    public void Update(GameInput input)
    {
        // the pointer objects are rebuilt every frame, so what is remembered between frames is their kind, not the objects
        var touches = new List<Pointer>(2);
        foreach (var p in input.Pointers)
        {
            if (p.Type == PointerType.Touch && p.ScreenPosition.HasValue && p.IsPressed) touches.Add(p);
        }

        // two fingers: pinch to zoom, and the midpoint pans
        if (touches.Count >= 2)
        {
            var a = touches[0].ScreenPosition!.Value;
            var b = touches[1].ScreenPosition!.Value;
            var mid = (a + b) * 0.5f;
            var distance = Vector2.Distance(a, b);
            if (!_pinching)
            {
                _pinching = true;
                _dragType = null;
                _lastPinch = distance;
                _view.BeginDrag(mid.X, mid.Y);
            }
            else if (_lastPinch > 1 && distance > 1)
            {
                _view.ZoomAt(mid.X, mid.Y, _lastPinch / distance);
                _lastPinch = distance;
                _view.BeginDrag(mid.X, mid.Y);
            }
            return;
        }
        if (_pinching)
        {
            // one finger is still down after a pinch: wait for it to lift rather than jump the chart
            _pinching = false;
            _dragType = null;
            return;
        }

        foreach (var p in input.Pointers)
        {
            if ((p.Type != PointerType.Mouse && p.Type != PointerType.Touch) || !p.ScreenPosition.HasValue) continue;
            var pos = p.ScreenPosition.Value;

            if (p.WasPressed && _dragType == null && !OverHud(pos))
            {
                _dragType = p.Type;
                _pressPosition = pos;
                _moved = false;
                _view.BeginDrag(pos.X, pos.Y);
            }
            else if (_dragType == p.Type && p.IsPressed)
            {
                if (!_moved && Vector2.Distance(pos, _pressPosition) > 6) _moved = true;
                if (_moved) _view.DragTo(pos.X, pos.Y);
            }
            if (p.WasReleased && _dragType == p.Type)
            {
                if (!_moved) Tapped?.Invoke(pos);
                _dragType = null;
            }
            if (p.Type == PointerType.Mouse && Math.Abs(p.ScrollDelta) > 0.01f && !OverHud(pos))
            {
                // wheel up (negative delta) zooms in
                _view.ZoomAt(pos.X, pos.Y, Math.Exp(p.ScrollDelta * 0.0012));
            }
        }
    }
}
