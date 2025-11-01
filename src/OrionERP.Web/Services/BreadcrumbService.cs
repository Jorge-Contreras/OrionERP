using System;
using System.Collections.Generic;

namespace OrionERP.Web.Services;

public interface IBreadcrumbService
{
  event Action? Changed;
  IReadOnlyList<BreadcrumbItem> Items { get; }
  void Set(params BreadcrumbItem[] items);
  void Set(IEnumerable<BreadcrumbItem> items);
  void Clear();
}

public sealed record BreadcrumbItem(string Text, string? Url = null, bool Active = false);

public sealed class BreadcrumbService : IBreadcrumbService
{
  private readonly List<BreadcrumbItem> _items = new();

  public event Action? Changed;

  public IReadOnlyList<BreadcrumbItem> Items => _items;

  public void Set(params BreadcrumbItem[] items)
    => Set((IEnumerable<BreadcrumbItem>)items);

  public void Set(IEnumerable<BreadcrumbItem> items)
  {
    _items.Clear();
    _items.AddRange(items);
    Changed?.Invoke();
  }

  public void Clear()
  {
    if (_items.Count == 0)
      return;

    _items.Clear();
    Changed?.Invoke();
  }
}
