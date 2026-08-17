using CitizenSim;
using NUnit.Framework;

namespace CitizenSim.Tests
{
    public class BtSchedulerTests
    {
        [Test]
        public void PerFrame_ZeroAgentsReturnsZero_OtherwiseMinOne()
        {
            Assert.AreEqual(0, BtScheduler.ComputePerFrame(0), "0 agent 不应安排 tick");
            Assert.AreEqual(1, BtScheduler.ComputePerFrame(1));
            Assert.AreEqual(1, BtScheduler.ComputePerFrame(29));
            Assert.AreEqual(1, BtScheduler.ComputePerFrame(30));
            Assert.AreEqual(16, BtScheduler.ComputePerFrame(500), "500 agent -> 500/30=16/帧");
            Assert.AreEqual(33, BtScheduler.ComputePerFrame(1000));
        }

        [Test]
        public void RoundRobin_CoversAllAgentsOverEnoughFrames()
        {
            // 纯逻辑复刻 Update 的 cursor 推进,验证 round-robin 在足够帧数后覆盖全部 agent。
            int agentCount = 500;
            int perFrame = BtScheduler.ComputePerFrame(agentCount);
            var ticked = new bool[agentCount];
            int cursor = 0;
            int frames = 40; // 40 帧 * 16/帧 = 640 > 500,必覆盖
            for (int f = 0; f < frames; f++)
            {
                for (int k = 0; k < perFrame; k++)
                {
                    if (cursor >= agentCount) cursor = 0;
                    ticked[cursor] = true;
                    cursor++;
                }
            }
            for (int i = 0; i < agentCount; i++)
                Assert.IsTrue(ticked[i], $"agent {i} 在 {frames} 帧内未被 tick");
        }

        [Test]
        public void ShouldPreempt_ThreatenedAndNotYetTicked_True()
        {
            Assert.IsTrue(BtScheduler.ShouldPreempt(true, 0, 1), "受威胁且本帧未 tick 应插队");
            Assert.IsTrue(BtScheduler.ShouldPreempt(true, 5, 9), "受威胁且上次 tick 在历史帧应插队");
        }

        [Test]
        public void ShouldPreempt_AlreadyTickedThisFrame_False()
        {
            Assert.IsFalse(BtScheduler.ShouldPreempt(true, 7, 7), "本帧已 tick 不应重复插队");
        }

        [Test]
        public void ShouldPreempt_NotThreatened_False()
        {
            Assert.IsFalse(BtScheduler.ShouldPreempt(false, 0, 1), "未受威胁不应插队");
            Assert.IsFalse(BtScheduler.ShouldPreempt(false, 7, 7), "未受威胁即便未 tick 也不插队");
        }
    }
}
