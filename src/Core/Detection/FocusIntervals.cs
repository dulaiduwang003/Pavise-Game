// @author bdth 2074055628@qq.com
// 文件用途 按前台状态过滤帧间隔 失焦帧与跨越切换边界的帧不计入统计

using System;

namespace PaviseApp
{
    internal sealed class FocusIntervalFilter
    {
        private bool focused = true;
        private bool dropNext;
        private long unfocusedUs;
        private int unfocusedFrames;

        public long UnfocusedUs { get { return unfocusedUs; } }
        public int UnfocusedFrames { get { return unfocusedFrames; } }

        public void Reset()
        {
            focused = true;
            dropNext = false;
            unfocusedUs = 0;
            unfocusedFrames = 0;
        }

        public void NoteFocus(bool nowFocused)
        {
            if (nowFocused == focused) return;
            focused = nowFocused;
            dropNext = true;
        }

        public bool Admit(int intervalUs)
        {
            if (focused && !dropNext) return true;
            dropNext = false;
            unfocusedUs += intervalUs;
            unfocusedFrames++;
            return false;
        }
    }
}
