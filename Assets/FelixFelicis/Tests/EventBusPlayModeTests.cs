using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Horcrux.Runtime.Utilities.EventBus;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FelixFelicis.Tests
{
    /// <summary>
    /// Kiểm chứng hành vi của <see cref="EventBus{T}"/> — thay cho script đọc Console bằng mắt.
    /// <para>
    /// <b>Vì sao PlayMode, không EditMode:</b> <see cref="EventBus{T}"/> là static và cố ý không có
    /// <c>Clear()</c>. EditMode test chạy trên domain của Editor, không reload giữa các lần chạy suite
    /// ⇒ chạy lần thứ hai là listener của lần đầu vẫn còn đăng ký, mọi khẳng định
    /// <c>ActiveListenerCount</c> thành vô nghĩa. Vào Play Mode thì domain reload đang bật
    /// (<c>m_EnterPlayModeOptions: 0</c>) ⇒ static về zero mỗi lần chạy.
    /// </para>
    /// <para>
    /// <b>Vì sao mỗi case một event type riêng:</b> domain reload chỉ dọn giữa các *lần chạy suite*,
    /// không dọn giữa từng <c>[Test]</c> — cả suite chạy trong một phiên Play Mode. Dùng chung một
    /// event type ⇒ listener của test trước rò sang test sau, và thứ tự chạy của NUnit là alphabet
    /// chứ không phải thứ tự khai báo. Một type riêng cho mỗi case khiến các test độc lập tuyệt đối.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class EventBusPlayModeTests
    {
        // Một type cho mỗi case — xem chú thích class.
        readonly struct SelfDisposeEvent    : IEvent { }
        readonly struct DisposeEarlierEvent : IEvent { }
        readonly struct DupEvent            : IEvent { }
        readonly struct LateSubEvent        : IEvent { }
        readonly struct NestedEvent         : IEvent { }
        readonly struct ThrowingEvent       : IEvent { }

        readonly struct LambdaEvent : IEvent
        {
            public readonly int Value;
            public LambdaEvent(int value) => Value = value;
        }

        /// <summary>Có field ref để chứng minh <c>default(T)</c> không gây NRE ở call site.</summary>
        readonly struct DefaultArgEvent : IEvent
        {
            public readonly string Key;
            public readonly int Value;

            public DefaultArgEvent(string key, int value)
            {
                Key = key;
                Value = value;
            }
        }

        // Dup-guard so delegate theo Target + Method ⇒ phải là method group, không phải hai lambda
        // (hai lambda viết giống nhau vẫn là hai delegate khác nhau). static ⇒ Target là null, không
        // bao giờ chạm nhánh auto-prune.
        static int dupHits;
        static void OnDupEvent(DupEvent e) => dupHits++;

        readonly List<GameObject> spawned = new();

        [TearDown]
        public void DestroySpawnedObjects()
        {
            foreach (var go in spawned)
            {
                if (go != null)
                    Object.DestroyImmediate(go);
            }

            spawned.Clear();
        }

        EventBusTestListener SpawnListener(string name, out GameObject owner)
        {
            owner = new GameObject(name);
            spawned.Add(owner);
            return owner.AddComponent<EventBusTestListener>();
        }

        // Case 1 — bug của bus cũ: RemoveAt dồn chỉ số làm listener kế bị bỏ sót, im lặng.
        [Test]
        public void Case1_ListenerDisposingItselfDoesNotSkipTheNextOne()
        {
            int hitA = 0, hitB = 0, hitC = 0;
            Subscription<SelfDisposeEvent> subA = default;

            subA = EventBus<SelfDisposeEvent>.Subscribe(_ =>
            {
                hitA++;
                subA.Dispose();
            });
            EventBus<SelfDisposeEvent>.Subscribe(_ => hitB++);
            EventBus<SelfDisposeEvent>.Subscribe(_ => hitC++);

            EventBus<SelfDisposeEvent>.Publish();

            Assert.AreEqual(1, hitA, "A phải chạy đúng một lần");
            Assert.AreEqual(1, hitB, "B bị bỏ sót — tombstone không giữ được chỉ số");
            Assert.AreEqual(1, hitC, "C bị bỏ sót");
            Assert.AreEqual(2, EventBus<SelfDisposeEvent>.ActiveListenerCount,
                "Sau Compact phải còn đúng B và C");
        }

        // Case 2 — listener sau huỷ listener trước: người đã chạy rồi, tombstone chỉ ảnh hưởng lần sau.
        [Test]
        public void Case2_DisposingAnEarlierListenerMidPublishRunsEveryoneOnce()
        {
            int hitA = 0, hitB = 0, hitC = 0;
            Subscription<DisposeEarlierEvent> subA = default;

            subA = EventBus<DisposeEarlierEvent>.Subscribe(_ => hitA++);
            EventBus<DisposeEarlierEvent>.Subscribe(_ => hitB++);
            EventBus<DisposeEarlierEvent>.Subscribe(_ =>
            {
                hitC++;
                subA.Dispose();
            });

            EventBus<DisposeEarlierEvent>.Publish();

            Assert.AreEqual(1, hitA, "A đã chạy trước khi bị tombstone");
            Assert.AreEqual(1, hitB, "B phải chạy đúng một lần");
            Assert.AreEqual(1, hitC, "C phải chạy đúng một lần");
            Assert.AreEqual(2, EventBus<DisposeEarlierEvent>.ActiveListenerCount,
                "Sau Compact phải còn đúng B và C");
        }

        // Case 3 — bus cũ chạy callback hai lần. Cả ActiveListenerCount lẫn LogError đều là hợp đồng.
        [Test]
        public void Case3_DuplicateSubscribeIsRejectedAndLogsError()
        {
            dupHits = 0;

            var first = EventBus<DupEvent>.Subscribe(OnDupEvent);
            Assert.AreEqual(1, EventBus<DupEvent>.ActiveListenerCount, "Đăng ký đầu phải nhận");

#if UNITY_EDITOR
            // Dup-guard nằm trong #if UNITY_EDITOR nên chỉ chờ log khi chạy trong Editor.
            // Không khai báo trước thì UTF tự fail test vì có LogError không mong đợi.
            LogAssert.Expect(LogType.Error, new Regex("Ignore duplicate subscription"));
#endif
            var duplicate = EventBus<DupEvent>.Subscribe(OnDupEvent);

            Assert.AreEqual(1, EventBus<DupEvent>.ActiveListenerCount,
                "Đăng ký trùng phải bị chặn, không nối thêm slot");

            EventBus<DupEvent>.Publish();
            Assert.AreEqual(1, dupHits, "Callback phải chạy đúng một lần");

            // Handle của lần trùng là default ⇒ Dispose không làm gì, không được huỷ oan bản gốc.
            duplicate.Dispose();
            EventBus<DupEvent>.Publish();
            Assert.AreEqual(2, dupHits, "Dispose handle rỗng không được huỷ đăng ký thật");

            first.Dispose();
        }

        // Case 4 — Publish chụp Count một lần: listener mới không làm vòng lặp tự nuôi.
        [Test]
        public void Case4_ListenerSubscribedMidPublishWaitsForTheNextPublish()
        {
            int outerHits = 0, lateHits = 0;

            EventBus<LateSubEvent>.Subscribe(_ =>
            {
                outerHits++;

                // Tên khác `_`: lambda lồng trùng tên tham số với lambda ngoài là CS0136.
                if (outerHits == 1)
                    EventBus<LateSubEvent>.Subscribe(late => lateHits++);
            });

            EventBus<LateSubEvent>.Publish();
            Assert.AreEqual(1, outerHits, "Listener gốc phải chạy");
            Assert.AreEqual(0, lateHits,
                "Listener đăng ký giữa lúc Publish KHÔNG được chạy ở lần đó");

            EventBus<LateSubEvent>.Publish();
            Assert.AreEqual(2, outerHits, "Listener gốc phải chạy lần hai");
            Assert.AreEqual(1, lateHits, "Listener mới phải chạy ở Publish kế");
        }

        // Case 5 — bus cũ spam MissingReferenceException mãi mãi.
        [Test]
        public void Case5_DestroyedOwnerIsPrunedAndNeverInvoked()
        {
            EventBusTestListener.ResetCounters();

            var listener = SpawnListener("Case5_DeadOwner", out var owner);
            EventBus<DeadOwnerEvent>.Subscribe(listener.OnDeadOwnerEvent);
            Assert.AreEqual(1, EventBus<DeadOwnerEvent>.ActiveListenerCount,
                "Đăng ký lúc owner còn sống phải nhận");

            Object.DestroyImmediate(owner);
            spawned.Remove(owner);

            // Hợp đồng của plan là "không exception thoát ra", không chỉ "không được gọi":
            // bus cũ ném MissingReferenceException ngay ở đây.
            Assert.DoesNotThrow(() => EventBus<DeadOwnerEvent>.Publish(),
                "Publish với owner đã destroy không được ném ra caller");

            Assert.AreEqual(0, EventBusTestListener.DeadOwnerHits,
                "Listener của owner đã destroy KHÔNG được gọi — prune phải chạy trước lúc gọi");
            Assert.AreEqual(0, EventBus<DeadOwnerEvent>.ActiveListenerCount,
                "Slot chết phải được dọn ở Publish kế");
        }

        // Case 6 — lambda có capture: auto-prune không thấy closure, Dispose là đường duy nhất.
        [Test]
        public void Case6_DisposingACapturingLambdaStopsIt()
        {
            int sum = 0;
            var sub = EventBus<LambdaEvent>.Subscribe(e => sum += e.Value);

            EventBus<LambdaEvent>.Publish(new LambdaEvent(5));
            Assert.AreEqual(5, sum, "Lambda có capture phải nhận được event");

            sub.Dispose();
            EventBus<LambdaEvent>.Publish(new LambdaEvent(7));

            Assert.AreEqual(5, sum, "Sau Dispose, lambda không được chạy nữa");
            Assert.AreEqual(0, EventBus<LambdaEvent>.ActiveListenerCount,
                "Sau Compact bus phải rỗng");
        }

        // Case 7 — lý do tồn tại của dispatchDepth: Compact ở cấp lồng sẽ dồn chỉ số của vòng ngoài
        // ⇒ bỏ sót listener (im lặng) rồi ArgumentOutOfRangeException (ném ra ngoài try/catch nội bộ).
        [Test]
        public void Case7_NestedPublishOfTheSameTypeSkipsNobodyAndRepeatsNobody()
        {
            int hitA = 0, hitB = 0;
            Subscription<NestedEvent> subA = default;

            subA = EventBus<NestedEvent>.Subscribe(_ =>
            {
                hitA++;
                subA.Dispose();                  // tombstone tại index 0
                EventBus<NestedEvent>.Publish(); // lồng đúng một cấp; A đã tombstone nên không đệ quy
            });
            EventBus<NestedEvent>.Subscribe(_ => hitB++);

            EventBus<NestedEvent>.Publish();

            // outer i=0: A → hitA=1, tombstone A, publish lồng
            //   inner: cnt=2; i=0 là tombstone → bỏ qua; i=1 là B → hitB=1. depth==1 ⇒ KHÔNG Compact
            // outer i=1: vẫn là B (chỉ số chưa bị dồn) → hitB=2. depth==0 ⇒ Compact
            Assert.AreEqual(1, hitA, "A tự huỷ nên chỉ chạy một lần");
            Assert.AreEqual(2, hitB,
                "B phải chạy 2 lần (một ở cấp lồng, một ở cấp ngoài) — Compact ở cấp lồng sẽ làm sai");
            Assert.AreEqual(1, EventBus<NestedEvent>.ActiveListenerCount,
                "Sau Compact phải còn đúng B");
        }

        // Case 8 — struct constraint: Publish() không tham số luôn hợp lệ.
        [Test]
        public void Case8_ParameterlessPublishDeliversDefaultWithoutThrowing()
        {
            bool invoked = false;
            string key = "sentinel";
            int value = -1;

            var sub = EventBus<DefaultArgEvent>.Subscribe(e =>
            {
                invoked = true;
                key = e.Key;
                value = e.Value;
            });

            EventBus<DefaultArgEvent>.Publish();

            Assert.IsTrue(invoked, "Listener phải được gọi với payload default");
            Assert.IsNull(key, "Field ref của struct zero-init phải là null");
            Assert.AreEqual(0, value, "Field value của struct zero-init phải là 0");

            sub.Dispose();
        }

        // Case 9 — KHÔNG có trong 8 case của plan. Nhánh auto-prune chỉ được kiểm ở phía "đã destroy";
        // không case nào kiểm phía "còn sống", nên một điều kiện viết ngược vẫn lọt qua cả suite —
        // dù nó làm mọi listener là method của MonoBehaviour không bao giờ nhận được event.
        [Test]
        public void Case9_LivingOwnerStillReceivesEvents()
        {
            EventBusTestListener.ResetCounters();

            var listener = SpawnListener("Case9_AliveOwner", out _);
            var sub = EventBus<AliveOwnerEvent>.Subscribe(listener.OnAliveOwnerEvent);

            EventBus<AliveOwnerEvent>.Publish();

            Assert.AreEqual(1, EventBusTestListener.AliveOwnerHits,
                "Owner còn sống phải nhận event — nếu bằng 0 thì IsOwnerDestroyed đang bị ngược");
            Assert.AreEqual(1, EventBus<AliveOwnerEvent>.ActiveListenerCount,
                "Owner còn sống không được bị prune");

            sub.Dispose();
        }

        // Case 10 — KHÔNG có trong plan. try/catch bọc TỪNG callback là hợp đồng đã chốt
        // ("1 listener chết ≠ kill listener khác") nhưng không case nào kiểm.
        [Test]
        public void Case10_AThrowingListenerDoesNotStopTheOthers()
        {
            int hitBefore = 0, hitAfter = 0;

            var subBefore = EventBus<ThrowingEvent>.Subscribe(_ => hitBefore++);
            var subThrow = EventBus<ThrowingEvent>.Subscribe(_ =>
                throw new InvalidOperationException("Listener no co chu dich"));
            var subAfter = EventBus<ThrowingEvent>.Subscribe(_ => hitAfter++);

            LogAssert.Expect(LogType.Exception, new Regex("Listener no co chu dich"));

            Assert.DoesNotThrow(() => EventBus<ThrowingEvent>.Publish(),
                "Exception của listener không được thoát ra caller");

            Assert.AreEqual(1, hitBefore, "Listener trước chỗ nổ phải chạy");
            Assert.AreEqual(1, hitAfter, "Listener sau chỗ nổ vẫn phải chạy");

            subBefore.Dispose();
            subThrow.Dispose();
            subAfter.Dispose();
        }

        // Case 11 — KHÔNG có trong plan. Case 5 và 9 chỉ có MỘT listener, nên không cái nào kiểm
        // được nhánh prune có làm lệch vòng duyệt hay không: `RemoveAt(i); continue;` đặt tombstone
        // rồi đi tiếp bằng chính `i` đó. Owner chết đứng TRƯỚC người còn sống là bố trí duy nhất
        // phơi ra chuyện đó — và cũng phân biệt được chiều của IsOwnerDestroyed một cách rõ ràng:
        // nếu nó lại bị viết ngược thì hits vẫn bằng 1, nhưng là hits của thằng đã chết.
        [Test]
        public void Case11_PruningADeadOwnerDoesNotSkipTheListenerAfterIt()
        {
            EventBusTestListener.ResetCounters();

            var deadListener = SpawnListener("Case11_DeadOwner", out var deadOwner);
            EventBus<PrunedNeighbourEvent>.Subscribe(deadListener.OnPrunedNeighbourEvent);

            // Người sống là lambda ⇒ đếm riêng, không dùng chung counter static với người chết,
            // nên khẳng định chỉ ra đúng ai đã chạy.
            int survivorHits = 0;
            var survivor = EventBus<PrunedNeighbourEvent>.Subscribe(_ => survivorHits++);

            Assert.AreEqual(2, EventBus<PrunedNeighbourEvent>.ActiveListenerCount,
                "Hai listener khác Target phải cùng đăng ký được");

            Object.DestroyImmediate(deadOwner);
            spawned.Remove(deadOwner);

            EventBus<PrunedNeighbourEvent>.Publish();

            Assert.AreEqual(0, EventBusTestListener.PrunedNeighbourHits,
                "Owner đã destroy ở index 0 không được gọi");
            Assert.AreEqual(1, survivorHits,
                "Listener ở index 1 bị bỏ sót — prune làm lệch vòng duyệt");
            Assert.AreEqual(1, EventBus<PrunedNeighbourEvent>.ActiveListenerCount,
                "Sau Compact chỉ còn người sống");

            survivor.Dispose();
        }
    }
}
