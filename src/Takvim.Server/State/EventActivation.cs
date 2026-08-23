using Microsoft.AspNetCore.Components;
using Takvim.Core.Recurrence;

namespace Takvim.Server.State;

/// <summary>
/// Bir etkinlik kartına tıklandığında taşınan bilgi.
/// Önizleme kartı, kendisini tıklanan kartın yanına konumlandırmak için
/// kaynağın ekrandaki yerini bilmek zorundadır.
/// </summary>
/// <param name="Occurrence">Tıklanan örnek.</param>
/// <param name="Element">Kartın DOM öğesi; konumlandırma ölçüsü buradan alınır.</param>
public sealed record EventActivation(EventOccurrence Occurrence, ElementReference Element);
