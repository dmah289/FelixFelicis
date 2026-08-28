using Horcrux.Runtime.Utilities.EventBus;
using UnityEngine;

namespace FelixFelicis.Tests
{
    /// <summary>Event cho case 5 — listener bị destroy trước khi Publish.</summary>
    public readonly struct DeadOwnerEvent : IEvent { }

    /// <summary>Event cho case 9 — listener còn sống lúc Publish.</summary>
    public readonly struct AliveOwnerEvent : IEvent { }

    /// <summary>
    /// Listener sống trên một <see cref="GameObject"/> thật, để kiểm nhánh auto-prune của
    /// <see cref="EventBus{T}"/> — nhánh duy nhất cần <c>Target</c> là <see cref="UnityEngine.Object"/>.
    /// <para>
    /// Bộ đếm là <c>static</c> vì case 5 gọi callback sau khi component đã destroy: đọc field
    /// instance lúc đó sẽ ném <c>MissingReferenceException</c> và làm lẫn nguyên nhân thất bại.
    /// </para>
    /// <para>
    /// File riêng, class top-level, tên file khớp tên class: <c>AddComponent&lt;T&gt;()</c> cần
    /// MonoScript khớp. MonoBehaviour khai báo lồng trong class khác sẽ fail runtime.
    /// </para>
    /// </summary>
    public sealed class EventBusTestListener : MonoBehaviour
    {
        public static int DeadOwnerHits;
        public static int AliveOwnerHits;

        public static void ResetCounters()
        {
            DeadOwnerHits = 0;
            AliveOwnerHits = 0;
        }

        public void OnDeadOwnerEvent(DeadOwnerEvent e) => DeadOwnerHits++;

        public void OnAliveOwnerEvent(AliveOwnerEvent e) => AliveOwnerHits++;
    }
}
