/* FlyknitBuddy 产品页：语言切换、进场动画、打字效果、场景切换、下载信息。不依赖任何外部库。 */
(function () {
  'use strict'

  // 产品页单独部署到别的网站时，在 <html data-api="http://10.0.0.5:8000"> 里写上服务端地址；
  // 由服务端直接提供（http://服务器:8000/）时留空即可
  var API = (document.documentElement.getAttribute('data-api') || '').replace(/\/$/, '')

  // ---------- 文案：中文就是页面里写的，越南语和英语在这里 ----------
  var DICT = {
    vi: {
      'nav.features': 'Tính năng', 'nav.scenes': 'Ứng dụng', 'nav.experts': 'Chuyên gia', 'nav.security': 'Bảo mật', 'nav.faq': 'Hỏi đáp', 'nav.download': 'Tải về',
      'hero.eyebrow': 'Trợ lý AI cho máy tính văn phòng nhà máy · 中文 / Tiếng Việt / English',
      'hero.title': 'Nói việc cần làm,<br /><span class="grad">phần còn lại để FlyknitBuddy lo</span>',
      'hero.lead': 'Sắp xếp tệp, phân tích Excel, viết email và báo cáo, dịch thuật, cài phần mềm, thao tác máy tính — chỉ cần nói bằng ngôn ngữ quen thuộc. Dữ liệu ở lại mạng nội bộ công ty, IT quản lý tập trung.',
      'hero.download': 'Tải bản Windows', 'hero.more': 'Xem nó làm được gì',
      'hero.b1': 'Windows 10 / 11', 'hero.b2': 'Triển khai trong mạng nội bộ', 'hero.b3': 'Đăng nhập một chạm bằng tài khoản domain',
      'float.f1t': 'Đã tạo Tổng hợp sản lượng tháng 9.xlsx', 'float.f1s': '3 xưởng · 30 ngày · có biểu đồ xu hướng',
      'float.f2s': 'Đã dịch sang tiếng Việt · giữ nguyên định dạng', 'float.f3t': 'Đã chặn lệnh nguy hiểm',
      'stats.s1': 'ngôn ngữ giao diện, chuyển đổi bất cứ lúc nào', 'stats.s2': 'chế độ: Làm việc, Trò chuyện, Dịch', 'stats.s3': 'dữ liệu nằm trên máy chủ nội bộ', 'stats.s4': 'tác vụ hẹn giờ, chạy cả sau giờ làm',
      'feat.kicker': 'Tính năng', 'feat.title': 'Một trợ lý, lo phần lớn việc vặt trong văn phòng',
      'feat.sub': 'Không cần học phần mềm mới, không cần viết công thức hay lệnh. Nói rõ điều bạn cần, nó tự chia bước, tự làm và giao kết quả cho bạn.',
      'feat.c1t': 'Làm việc: thao tác máy tính thay bạn', 'feat.c1p': 'Đọc ghi tệp, sắp xếp thư mục, chạy lệnh, mở phần mềm. Mỗi bước đều hiển thị rõ, tự sao lưu trước khi sửa, tệp xóa vào Thùng rác.',
      'feat.c2t': 'Excel / Word / PPT', 'feat.c2p': 'Tổng hợp báo cáo, vẽ biểu đồ, viết báo cáo tuần, làm slide thuyết trình. Tạo tệp trực tiếp trong thư mục bạn chọn.',
      'feat.c3t': 'Dịch Trung – Việt – Anh', 'feat.c3p': 'Dịch cả đoạn, cả tệp, giữ nguyên định dạng. Bảng thuật ngữ nhà máy giúp dùng từ thống nhất; chọn chữ rồi nhấn Ctrl+Alt+T để dịch ngay.',
      'feat.c4t': 'Chuyên gia', 'feat.c4p': 'Các chuyên gia AI do IT cấu hình cho sản xuất, QC, hành chính… hiểu quy trình và thuật ngữ, chọn một người rồi nói việc.',
      'feat.c5t': 'Thư viện', 'feat.c5p': 'Tệp đã dùng và đã tạo trong hội thoại được lưu tự động, phân loại theo ảnh, tài liệu, yêu thích; lần sau chỉ cần một câu là tìm thấy.',
      'feat.c6t': 'Kỹ năng & Kết nối', 'feat.c6p': 'Cài quy trình chuẩn từ thư viện kỹ năng của công ty; kết nối MES, ERP để AI tra dữ liệu và xử lý nghiệp vụ trực tiếp.',
      'feat.c7t': 'Tác vụ hẹn giờ', 'feat.c7p': 'Mỗi sáng tổng hợp sản lượng hôm qua, mỗi thứ Sáu làm báo cáo QC — đặt giờ, đến giờ tự chạy, xong thì báo cho bạn.',
      'feat.c8t': 'Càng dùng càng hiểu bạn', 'feat.c8p': 'Ghi nhớ thói quen và thông tin hay dùng; việc tương tự làm nhiều lần sẽ được đúc kết thành kỹ năng, lần sau nhanh và chuẩn hơn.',
      'agent.kicker': 'Chế độ Làm việc', 'agent.title': 'Từ một câu nói đến một bản báo cáo nộp được ngay',
      'agent.sub': 'Nó xem kỹ tệp, chia bước rồi làm từng bước. Khi cần động vào tệp quan trọng sẽ hỏi bạn trước; xong việc giao cả tệp và kết luận.',
      'agent.s1t': 'Nói việc cần làm', 'agent.s1p': '"Tổng hợp nhật ký tháng 9 trong D:\\日报 theo xưởng, rồi vẽ biểu đồ xu hướng"',
      'agent.s2t': 'Tự chia bước và làm', 'agent.s2p': 'Đọc 30 tệp nhật ký → tổng hợp theo xưởng → tạo Excel → vẽ biểu đồ',
      'agent.s3t': 'Kết quả trao tận tay', 'agent.s3p': 'Tệp lưu trong không gian làm việc, biểu đồ và kết luận hiện ngay trong hội thoại',
      'agent.win': 'Làm việc · Tổng hợp sản lượng tháng 9',
      'tr.kicker': 'Dịch thuật', 'tr.title': 'Trung, Việt, Anh — xưởng và văn phòng nói cùng một ngôn ngữ',
      'tr.sub': 'Thông báo, bảng phân ca, email, cả tệp Word và Excel đều dịch được. Nếu bản gốc đã là ngôn ngữ của bạn, tự đổi sang ngôn ngữ còn lại.',
      'tr.t1': 'Bảng thuật ngữ nhà máy giúp dùng từ chuyên nghiệp, thống nhất', 'tr.t2': 'Chọn chữ trong bất kỳ phần mềm nào, Ctrl+Alt+T là có bản dịch', 'tr.t3': 'Nhấp chuột phải vào tệp trong File Explorer → "Dịch bằng FlyknitBuddy"',
      'ex.kicker': 'Chuyên gia', 'ex.title': 'Chuyên gia nghiệp vụ của công ty, ai cũng gọi được',
      'ex.sub': 'IT cấu hình chuyên gia theo nghiệp vụ: cách làm việc, kiến thức, hệ thống được dùng — cấu hình một lần, cả công ty dùng. Nhân viên mới cũng làm việc như người có kinh nghiệm.',
      'ex.c1': 'Phân tích sản xuất MES', 'ex.c2': 'Phân tích dữ liệu QC', 'ex.c3': 'Lập kế hoạch sản xuất', 'ex.c4': 'Vận hành IT', 'ex.c5': 'Hướng dẫn ERP', 'ex.c6': 'Trợ lý báo cáo', 'ex.win': 'Chuyên gia',
      'sc.kicker': 'Ứng dụng', 'sc.title': 'Vị trí nào cũng có chỗ dùng', 'sc.sub': 'Chọn một vị trí để xem mọi người thường nhờ nó làm gì.',
      'sc.prod': 'Sản xuất', 'sc.qc': 'QC', 'sc.office': 'Hành chính / Nhân sự', 'sc.buy': 'Mua hàng / Kinh doanh', 'sc.it': 'IT',
      'sc.p1': 'Tổng hợp nhật ký sản lượng hôm qua của ba xưởng thành một bảng, đánh dấu chuyền chưa đạt',
      'sc.p2': 'Dựa vào hạn giao đơn tuần sau, lập bản nháp kế hoạch sản xuất cho từng chuyền',
      'sc.p3': 'Mỗi sáng 8 giờ đưa sản lượng và thời gian dừng máy hôm qua vào thư viện của tôi',
      'sc.q1': 'Phân tích dữ liệu QC tháng 9, vẽ biểu đồ Pareto theo loại lỗi',
      'sc.q2': 'Dịch khiếu nại khách hàng này sang tiếng Trung và soạn bản nháp báo cáo 8D',
      'sc.q3': 'Mỗi chiều thứ Sáu tạo báo cáo QC tuần, lưu vào D:\\报表',
      'sc.o1': 'Dịch thông báo này sang tiếng Việt, dàn trang thành tệp Word để in dán',
      'sc.o2': 'Sắp xếp bảng chấm công tháng này, thống kê giờ tăng ca của từng bộ phận',
      'sc.o3': 'Tổng hợp mấy bản ghi cuộc họp này thành biên bản, liệt kê việc cần làm và người phụ trách',
      'sc.b1': 'Viết email tiếng Anh hỏi nhà cung cấp có thể giao vải tháng 10 sớm một tuần không',
      'sc.b2': 'So sánh ba bảng báo giá này: đơn giá, thời gian giao, điều kiện thanh toán',
      'sc.b3': 'Từ dữ liệu đơn hàng này làm một bộ slide báo cáo quý',
      'sc.i1': 'Máy in không kết nối được, kiểm tra giúp tôi lỗi gì',
      'sc.i2': 'Cài ERP client từ ổ chia sẻ, cài xong thì mở lên',
      'sc.i3': 'Ổ C sắp đầy, xem tệp lớn nào có thể dọn (liệt kê trước, đừng xóa)',
      'lib.kicker': 'Thư viện', 'lib.title': 'Những gì đã làm, đều ở đây', 'lib.win': 'Thư viện',
      'lib.sub': 'Tệp gửi cho AI, báo cáo và tài liệu nó tạo, ảnh chụp màn hình đều được lưu tự động. Xem theo yêu thích, thư mục, hình ảnh, hoặc nói thẳng "sửa lại bảng sản lượng tuần trước".',
      'hand.kicker': 'Luôn trong tầm tay', 'hand.title': 'Không cần chuyển cửa sổ, ở đâu cũng gọi được',
      'hand.c1t': 'Quả bóng nổi', 'hand.c1p': 'Nhấp để mở, kéo tệp thả vào bóng là gửi cho AI; nhấp chuột phải có cửa sổ trò chuyện nhỏ, dịch nhanh, hỏi bằng ảnh chụp.',
      'hand.c2t': 'Dịch nhanh', 'hand.c2p': 'Chọn chữ trong bất kỳ phần mềm nào, nhấn <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>T</kbd>, bản dịch hiện cạnh con trỏ.',
      'hand.c3t': 'Hỏi bằng ảnh chụp', 'hand.c3p': 'Nhấn <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>S</kbd>, khoanh vùng lỗi hay bảng biểu khó hiểu trên màn hình rồi hỏi ngay.',
      'hand.c4t': 'Menu chuột phải', 'hand.c4p': 'Nhấp chuột phải vào tệp: "Hỏi / Tóm tắt / Dịch bằng FlyknitBuddy"; trong thư mục: "Làm việc ở đây".',
      'sec.kicker': 'An toàn, kiểm soát được', 'sec.title': 'Làm được việc, và tuân thủ quy tắc',
      'sec.sub': 'AI có thể thao tác máy tính nên lớp nào cũng có hàng rào bảo vệ. Quy tắc do IT cấu hình tập trung, áp dụng ngay cho mọi máy.',
      'sec.c1t': 'Dữ liệu không ra khỏi nhà máy', 'sec.c1p': 'Máy chủ và cổng mô hình đặt trong mạng nội bộ; hội thoại, tệp, ghi nhớ nằm trên máy chủ công ty và máy nhân viên.',
      'sec.c2t': 'Cách ly không gian làm việc', 'sec.c2p': 'Mặc định chỉ đọc ghi trong thư mục bạn chọn, ra ngoài phải hỏi bạn; truy cập mạng theo danh sách cho phép.',
      'sec.c3t': 'Chặn lệnh nguy hiểm', 'sec.c3p': 'Định dạng ổ, xóa hàng loạt, sửa registry… bị chặn ngay; thao tác quan trọng phải được bạn xác nhận.',
      'sec.c4t': 'Sửa sai vẫn khôi phục được', 'sec.c4p': 'Tự sao lưu trước khi sửa tệp, tệp xóa vào Thùng rác, khôi phục bất cứ lúc nào.',
      'sec.c5t': 'Ghi lại mọi thao tác', 'sec.c5p': 'Mỗi lần chạy lệnh, ghi tệp, bị chặn đều vào nhật ký kiểm tra; nhân viên tự xem được, IT tra cứu được.',
      'sec.c6t': 'IT quản lý tập trung', 'sec.c6p': 'Mô hình, hạn mức, chuyên gia, kỹ năng, chính sách bảo mật cấu hình một chỗ; phiên bản mới tự cập nhật, không cần cài từng máy.',
      'dl.title': 'Bắt đầu dùng FlyknitBuddy ngay', 'dl.sub': 'Cài xong đăng nhập một chạm bằng tài khoản domain công ty, không cần nhập địa chỉ máy chủ.',
      'dl.exe': 'Tải bộ cài (.exe)', 'dl.zip': 'Bản không cần cài (.zip)', 'dl.checking': 'Đang kiểm tra phiên bản mới nhất…',
      'dl.closed': 'Chưa mở tải về. Vui lòng liên hệ phòng IT để nhận bộ cài.',
      'dl.s1': 'Tải và chạy bộ cài', 'dl.s2': 'Đăng nhập một chạm bằng tài khoản domain', 'dl.s3': 'Nhấp quả bóng nổi góc phải dưới và nói việc cần làm',
      'dl.req': 'Yêu cầu: Windows 10 / 11 (64-bit) · cần kết nối mạng nội bộ công ty · bộ cài cần quyền quản trị; nếu không có quyền, dùng bản không cần cài',
      'dl.version': 'Phiên bản mới nhất {v} · {size} · phát hành {date}',
      'faq.kicker': 'Hỏi đáp', 'faq.title': 'Có thể bạn muốn biết',
      'faq.q1': 'Có mất phí không? Có phải tự đăng ký tài khoản không?', 'faq.a1': 'Không. FlyknitBuddy là công cụ nội bộ, cài xong đăng nhập bằng tài khoản domain công ty. Mỗi ngày có hạn mức sử dụng, dùng hết có thể liên hệ IT để điều chỉnh.',
      'faq.q2': 'Hội thoại và tệp của tôi có bị người khác xem không?', 'faq.a2': 'Hội thoại và tệp lưu trên máy của bạn và máy chủ nội bộ, không gửi ra ngoài công ty. Theo quy định công ty, IT có thể tra cứu nhật ký kiểm tra khi cần.',
      'faq.q3': 'AI có xóa nhầm tệp của tôi không?', 'faq.a3': 'Mặc định chỉ thao tác trong không gian làm việc bạn chọn; tệp xóa vào Thùng rác, sửa tệp có sao lưu trước; thao tác nguy hiểm bị chặn, thao tác quan trọng hỏi bạn trước.',
      'faq.q4': 'Không có quyền quản trị có cài được không?', 'faq.a4': 'Được, tải bản không cần cài (.zip), giải nén rồi chạy FlyknitBuddy.exe. Bộ cài (.exe) cần quyền quản trị và sẽ tự cập nhật sau khi cài.',
      'faq.q5': 'Hỗ trợ những ngôn ngữ nào?', 'faq.a5': 'Giao diện có tiếng Trung, tiếng Việt, tiếng Anh, đổi bất cứ lúc nào trong Cài đặt. Hỏi bằng ngôn ngữ nào thì trả lời bằng ngôn ngữ đó.',
      'faq.q6': 'Gặp sự cố thì hỏi ai?', 'faq.a6': 'Nhấn "Phản hồi" trong chương trình để gửi vấn đề và ảnh chụp cho IT, hoặc liên hệ phòng Công nghệ thông tin.',
      'foot.tag': 'Trợ lý AI cho máy tính văn phòng nhà máy',
    },
    en: {
      'nav.features': 'Features', 'nav.scenes': 'Use cases', 'nav.experts': 'Experts', 'nav.security': 'Security', 'nav.faq': 'FAQ', 'nav.download': 'Download',
      'hero.eyebrow': 'The AI assistant for factory office PCs · 中文 / Tiếng Việt / English',
      'hero.title': 'Say what needs doing.<br /><span class="grad">FlyknitBuddy handles the rest.</span>',
      'hero.lead': 'Organize files, analyze Excel, write emails and reports, translate, install software and operate your PC — just ask in the language you use every day. Your data stays on the company network, managed by IT.',
      'hero.download': 'Download for Windows', 'hero.more': 'See what it can do',
      'hero.b1': 'Windows 10 / 11', 'hero.b2': 'Runs on the company intranet', 'hero.b3': 'One-click domain sign-in',
      'float.f1t': 'Created September output summary.xlsx', 'float.f1s': '3 workshops · 30 days · with trend chart',
      'float.f2s': 'Translated to Vietnamese · formatting kept', 'float.f3t': 'Dangerous command blocked',
      'stats.s1': 'interface languages, switch anytime', 'stats.s2': 'modes: Agent, Chat, Translate', 'stats.s3': 'of data stays on internal servers', 'stats.s4': 'scheduled tasks, even after hours',
      'feat.kicker': 'Features', 'feat.title': 'One assistant for most of the busywork in the office',
      'feat.sub': 'No new software to learn, no formulas or commands to write. Say what you need and it plans the steps, does the work and hands you the result.',
      'feat.c1t': 'Agent: works your PC for you', 'feat.c1p': 'Reads and writes files, organizes folders, runs commands and opens apps. Every step is visible, files are backed up before edits and deletions go to the Recycle Bin.',
      'feat.c2t': 'Excel / Word / PPT', 'feat.c2p': 'Summarize reports, draw charts, write weekly reports and build slide decks — saved straight to the folder you choose.',
      'feat.c3t': 'Chinese ⇄ Vietnamese ⇄ English', 'feat.c3p': 'Translate passages or whole files with formatting intact. A factory glossary keeps terms consistent; select text and press Ctrl+Alt+T to translate in place.',
      'feat.c4t': 'Experts', 'feat.c4p': 'AI experts configured by IT for production, QC, admin and more. They know the processes and terms — pick one and describe the job.',
      'feat.c5t': 'Library', 'feat.c5p': 'Files you send and files it creates are saved automatically, sorted by images, documents and favorites, so one sentence brings them back.',
      'feat.c6t': 'Skills & connectors', 'feat.c6p': 'Install standard procedures from the company skill library; connect MES, ERP and other systems so the AI can query data and get work done.',
      'feat.c7t': 'Scheduled tasks', 'feat.c7p': 'Summarize yesterday\u2019s output every morning, produce the QC report every Friday — set a time and it runs and notifies you.',
      'feat.c8t': 'Learns how you work', 'feat.c8p': 'Remembers your preferences and common details; tasks it has done well become reusable skills, faster and more accurate next time.',
      'agent.kicker': 'Agent mode', 'agent.title': 'From one sentence to a report you can hand in',
      'agent.sub': 'It inspects the files, plans the steps and works through them. It asks before touching anything important and delivers both the files and the conclusions.',
      'agent.s1t': 'Describe the job', 'agent.s1p': '"Summarize September\u2019s daily reports in D:\\日报 by workshop and draw a trend chart"',
      'agent.s2t': 'It plans and does the work', 'agent.s2p': 'Read 30 daily reports → total by workshop → build the Excel file → draw the chart',
      'agent.s3t': 'Results in your hands', 'agent.s3p': 'Files are saved in your workspace; the chart and summary appear right in the chat',
      'agent.win': 'Agent · September output summary',
      'tr.kicker': 'Translation', 'tr.title': 'Chinese, Vietnamese, English — the shop floor and the office finally speak the same language',
      'tr.sub': 'Notices, shift rosters, emails and entire Word or Excel files. If the source is already in your language, it switches to the other one.',
      'tr.t1': 'Factory glossary for consistent, professional wording', 'tr.t2': 'Select text in any app and press Ctrl+Alt+T for an instant translation', 'tr.t3': 'Right-click a file in File Explorer → "Translate with FlyknitBuddy"',
      'ex.kicker': 'Experts', 'ex.title': 'Your company\u2019s domain experts, on call for everyone',
      'ex.sub': 'IT configures experts per business area — how they work, what they know, which systems they may use. Set up once, used by everyone; new hires work like veterans.',
      'ex.c1': 'MES production analysis', 'ex.c2': 'QC data analysis', 'ex.c3': 'Production scheduling', 'ex.c4': 'IT support', 'ex.c5': 'ERP how-to', 'ex.c6': 'Report assistant', 'ex.win': 'Experts',
      'sc.kicker': 'Use cases', 'sc.title': 'Useful in every role', 'sc.sub': 'Pick a role to see what people ask it to do.',
      'sc.prod': 'Production', 'sc.qc': 'Quality', 'sc.office': 'Admin / HR', 'sc.buy': 'Purchasing / Sales', 'sc.it': 'IT',
      'sc.p1': 'Combine yesterday\u2019s output reports from the three workshops into one table and flag lines below target',
      'sc.p2': 'Draft a production plan for each line based on next week\u2019s order due dates',
      'sc.p3': 'Every morning at 8, put yesterday\u2019s output and machine downtime in my library',
      'sc.q1': 'Analyze September\u2019s QC data and draw a Pareto chart by defect type',
      'sc.q2': 'Translate this customer complaint into Chinese and draft an 8D report',
      'sc.q3': 'Every Friday afternoon, generate the weekly QC report and save it to D:\\报表',
      'sc.o1': 'Translate this notice into Vietnamese and lay it out as a printable Word document',
      'sc.o2': 'Tidy up this month\u2019s attendance sheet and total overtime hours by department',
      'sc.o3': 'Turn these meeting notes into minutes with action items and owners',
      'sc.b1': 'Write an English email asking the supplier whether October\u2019s fabric can ship a week earlier',
      'sc.b2': 'Compare these three quotations: unit price, lead time and payment terms',
      'sc.b3': 'Build a quarterly review deck from this order data',
      'sc.i1': 'The printer won\u2019t connect — help me find out why',
      'sc.i2': 'Install the ERP client from the shared drive and open it',
      'sc.i3': 'Drive C is almost full — list the large files that could be cleaned up (don\u2019t delete yet)',
      'lib.kicker': 'Library', 'lib.title': 'Everything you\u2019ve made, in one place', 'lib.win': 'Library',
      'lib.sub': 'Attachments you send, reports and documents it creates, and screenshots are filed automatically. Browse favorites, folders or images — or just say "update last week\u2019s output sheet".',
      'hand.kicker': 'Always at hand', 'hand.title': 'No window switching — call it from anywhere',
      'hand.c1t': 'Floating ball', 'hand.c1p': 'Click to open, drop files on it to send them to the AI; right-click for the mini chat window, quick translate and screenshot questions.',
      'hand.c2t': 'Quick translate', 'hand.c2p': 'Select text in any app and press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>T</kbd> — the translation pops up by your cursor.',
      'hand.c3t': 'Ask with a screenshot', 'hand.c3p': 'Press <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>S</kbd>, select a confusing error or table on screen and ask about it.',
      'hand.c4t': 'Right-click menu', 'hand.c4p': 'Right-click a file to "Ask / Summarize / Translate with FlyknitBuddy", or a folder to "Work here".',
      'sec.kicker': 'Safe and in control', 'sec.title': 'Gets things done — and follows the rules',
      'sec.sub': 'Because the AI can operate a PC, every layer has guardrails. Rules are set centrally by IT and take effect on every PC immediately.',
      'sec.c1t': 'Data stays in the factory', 'sec.c1p': 'The server and model gateway run on the company network; chats, files and memories live on company servers and employee PCs.',
      'sec.c2t': 'Workspace isolation', 'sec.c2p': 'By default it only reads and writes inside the folder you choose and asks before going further; network access follows an allowlist.',
      'sec.c3t': 'Dangerous commands blocked', 'sec.c3p': 'Formatting drives, mass deletion, registry edits and the like are stopped outright; important actions need your confirmation.',
      'sec.c4t': 'Mistakes are reversible', 'sec.c4p': 'Files are backed up before edits and deletions go to the Recycle Bin, so everything can be restored.',
      'sec.c5t': 'Full audit trail', 'sec.c5p': 'Every command, file write and block is logged — visible to the employee and searchable by IT.',
      'sec.c6t': 'Centrally managed by IT', 'sec.c6p': 'Models, quotas, experts, skills and security policy in one admin console; new versions roll out automatically.',
      'dl.title': 'Start using FlyknitBuddy today', 'dl.sub': 'Install it and sign in with your company domain account — no server address to type.',
      'dl.exe': 'Download installer (.exe)', 'dl.zip': 'Portable version (.zip)', 'dl.checking': 'Checking the latest version…',
      'dl.closed': 'Downloads aren\u2019t open yet. Please contact the IT department for the installer.',
      'dl.s1': 'Download and run the installer', 'dl.s2': 'Sign in with your domain account', 'dl.s3': 'Click the floating ball and say what you need',
      'dl.req': 'Requirements: Windows 10 / 11 (64-bit) · company network access · the installer needs administrator rights; use the portable version otherwise',
      'dl.version': 'Latest version {v} · {size} · released {date}',
      'faq.kicker': 'FAQ', 'faq.title': 'Questions you may have',
      'faq.q1': 'Does it cost anything? Do I need to sign up?', 'faq.a1': 'No. FlyknitBuddy is an internal tool: install it and sign in with your company domain account. There is a daily usage quota; contact IT if you need more.',
      'faq.q2': 'Can others see my chats and files?', 'faq.a2': 'Chats and files are stored on your PC and the company\u2019s internal server and never leave the company. Under company policy, IT may review audit records when necessary.',
      'faq.q3': 'Could the AI delete my files by mistake?', 'faq.a3': 'By default it only works inside the workspace you choose; deleted files go to the Recycle Bin and edits are backed up first. Dangerous actions are blocked and important ones need your OK.',
      'faq.q4': 'Can I install it without administrator rights?', 'faq.a4': 'Yes — download the portable version (.zip), unzip it and run FlyknitBuddy.exe. The installer (.exe) needs administrator rights and keeps itself up to date.',
      'faq.q5': 'Which languages does it support?', 'faq.a5': 'The interface is available in Chinese, Vietnamese and English, switchable in Settings. It answers in the language you ask in.',
      'faq.q6': 'Who do I contact for help?', 'faq.a6': 'Click "Feedback" in the app to send the problem and a screenshot to IT, or contact the IT department.',
      'foot.tag': 'The AI assistant for factory office PCs',
    },
  }
  DICT.zh = { 'dl.version': '最新版本 {v} · {size} · {date} 发布' }

  var TYPING = {
    zh: ['把 D:\\日报 里 9 月的日报按车间汇总成一张 Excel 表', '把这份通知翻译成越南语，排版成可以打印的 Word', '每周五下午生成质检周报，保存到 D:\\报表', '给供应商写一封英文邮件，询问 10 月面料交期'],
    vi: ['Tổng hợp nhật ký tháng 9 trong D:\\日报 theo xưởng thành một bảng Excel', 'Dịch thông báo này sang tiếng Việt và dàn trang Word để in', 'Mỗi chiều thứ Sáu tạo báo cáo QC tuần, lưu vào D:\\报表', 'Viết email tiếng Anh hỏi nhà cung cấp về hạn giao vải tháng 10'],
    en: ['Summarize September\u2019s daily reports in D:\\日报 by workshop into one Excel sheet', 'Translate this notice into Vietnamese as a printable Word file', 'Every Friday afternoon, generate the weekly QC report', 'Write an English email asking the supplier about October\u2019s fabric delivery'],
  }

  var HTML_LANG = { zh: 'zh-CN', vi: 'vi', en: 'en' }
  var lang = 'zh'
  var release = null

  function storage(key, value) {
    try {
      if (value === undefined) return localStorage.getItem(key)
      localStorage.setItem(key, value)
    } catch (e) { /* 隐私模式等存不了，没关系 */ }
    return null
  }

  // 中文原文从页面上记下来，切回中文时用
  var original = {}
  document.querySelectorAll('[data-i18n]').forEach(function (el) { original[el.getAttribute('data-i18n')] = el.textContent })
  document.querySelectorAll('[data-i18n-html]').forEach(function (el) { original[el.getAttribute('data-i18n-html')] = el.innerHTML })
  // 带 <kbd> 的说明也按 HTML 处理
  document.querySelectorAll('[data-i18n]').forEach(function (el) {
    if (el.querySelector('kbd')) original[el.getAttribute('data-i18n')] = el.innerHTML
  })

  function t(key) {
    var d = DICT[lang] || {}
    return d[key] != null ? d[key] : (DICT.zh[key] != null ? DICT.zh[key] : original[key])
  }

  function applyLang(next) {
    lang = DICT[next] || next === 'zh' ? next : 'zh'
    document.documentElement.lang = HTML_LANG[lang]
    document.querySelectorAll('[data-i18n]').forEach(function (el) {
      var key = el.getAttribute('data-i18n')
      var text = t(key)
      if (text == null) return
      if (/<kbd>/.test(text)) el.innerHTML = text
      else el.textContent = text
    })
    document.querySelectorAll('[data-i18n-html]').forEach(function (el) {
      var text = t(el.getAttribute('data-i18n-html'))
      if (text != null) el.innerHTML = text
    })
    document.querySelectorAll('.lang button').forEach(function (b) { b.classList.toggle('on', b.getAttribute('data-lang') === lang) })
    renderRelease()
    restartTyping()
    storage('flyknit-landing-lang', lang)
  }

  document.querySelectorAll('.lang button').forEach(function (b) {
    b.addEventListener('click', function () { applyLang(b.getAttribute('data-lang')) })
  })

  // ---------- 导航 ----------
  var nav = document.querySelector('.nav')
  var menuBtn = document.querySelector('.menu-btn')
  function onScroll() { nav.classList.toggle('scrolled', window.scrollY > 8) }
  window.addEventListener('scroll', onScroll, { passive: true })
  onScroll()
  menuBtn.addEventListener('click', function () {
    var open = nav.classList.toggle('open')
    menuBtn.setAttribute('aria-expanded', String(open))
  })
  document.querySelectorAll('.nav-links a').forEach(function (a) {
    a.addEventListener('click', function () { nav.classList.remove('open'); menuBtn.setAttribute('aria-expanded', 'false') })
  })

  // ---------- 进场动画 ----------
  var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches
  var targets = document.querySelectorAll('.reveal, .tr-demo')
  if ('IntersectionObserver' in window && !reduce) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (!e.isIntersecting) return
        e.target.classList.add('in')
        e.target.querySelectorAll('.count').forEach(countUp)
        if (e.target.classList.contains('stat')) e.target.querySelectorAll('.count').forEach(countUp)
        io.unobserve(e.target)
      })
    }, { threshold: 0.15, rootMargin: '0px 0px -40px 0px' })
    targets.forEach(function (el) { io.observe(el) })
  } else {
    targets.forEach(function (el) { el.classList.add('in') })
  }

  function countUp(el) {
    if (el.dataset.done) return
    el.dataset.done = '1'
    var to = Number(el.getAttribute('data-to')) || 0
    var start = performance.now()
    var dur = 1200
    function step(now) {
      var p = Math.min(1, (now - start) / dur)
      el.textContent = String(Math.round(to * (1 - Math.pow(1 - p, 3))))
      if (p < 1) requestAnimationFrame(step)
    }
    requestAnimationFrame(step)
  }

  // ---------- 打字效果 ----------
  var typingEl = document.getElementById('typing')
  var typingTimer = null
  function restartTyping() {
    clearTimeout(typingTimer)
    var lines = TYPING[lang] || TYPING.zh
    if (reduce) { typingEl.textContent = lines[0]; return }
    var li = 0, ci = 0, deleting = false
    function tick() {
      var line = lines[li]
      if (!deleting) {
        ci++
        typingEl.textContent = line.slice(0, ci)
        if (ci >= line.length) { deleting = true; typingTimer = setTimeout(tick, 2200); return }
        typingTimer = setTimeout(tick, 55 + Math.random() * 45)
      } else {
        ci = Math.max(0, ci - 3)
        typingEl.textContent = line.slice(0, ci)
        if (ci === 0) { deleting = false; li = (li + 1) % lines.length; typingTimer = setTimeout(tick, 350); return }
        typingTimer = setTimeout(tick, 22)
      }
    }
    tick()
  }

  // ---------- 场景切换 ----------
  document.querySelectorAll('.tabs button').forEach(function (b) {
    b.addEventListener('click', function () {
      var name = b.getAttribute('data-tab')
      document.querySelectorAll('.tabs button').forEach(function (x) {
        x.classList.toggle('on', x === b)
        x.setAttribute('aria-selected', String(x === b))
      })
      document.querySelectorAll('.panel').forEach(function (p) { p.classList.toggle('on', p.getAttribute('data-panel') === name) })
    })
  })

  // ---------- 下载信息 ----------
  var exeBtn = document.getElementById('dl-exe')
  var zipBtn = document.getElementById('dl-zip')
  var meta = document.getElementById('dl-meta')
  var closed = document.getElementById('dl-closed')
  exeBtn.href = API + '/api/v1/public/download?format=exe'
  zipBtn.href = API + '/api/v1/public/download?format=zip'

  function size(bytes) {
    if (!bytes) return ''
    return bytes >= 1048576 ? (bytes / 1048576).toFixed(0) + ' MB' : Math.max(1, Math.round(bytes / 1024)) + ' KB'
  }

  function renderRelease() {
    if (release === null) return
    var ok = release && release.download_enabled
    closed.hidden = !!ok
    meta.hidden = !ok
    var formats = (ok && release.formats) || []
    exeBtn.classList.toggle('disabled', formats.indexOf('exe') < 0)
    zipBtn.classList.toggle('disabled', formats.indexOf('zip') < 0)
    // 只有免安装版时，把它当主按钮
    zipBtn.classList.toggle('primary', formats.length === 1 && formats[0] === 'zip')
    zipBtn.classList.toggle('ghost', !(formats.length === 1 && formats[0] === 'zip'))
    exeBtn.hidden = ok && formats.indexOf('exe') < 0
    if (ok) {
      var date = release.published_at ? new Date(release.published_at).toLocaleDateString(HTML_LANG[lang]) : ''
      meta.textContent = t('dl.version').replace('{v}', release.version).replace('{size}', size(release.size)).replace('{date}', date)
    }
  }

  fetch(API + '/api/v1/public/release', { cache: 'no-store' })
    .then(function (r) { return r.ok ? r.json() : { download_enabled: false } })
    .catch(function () { return { download_enabled: false } })
    .then(function (data) { release = data; renderRelease() })

  document.getElementById('year').textContent = String(new Date().getFullYear())

  // ---------- 初始语言：上次选的 > 浏览器语言 > 中文 ----------
  var saved = storage('flyknit-landing-lang')
  var nav0 = (navigator.language || '').toLowerCase()
  applyLang(saved || (nav0.indexOf('vi') === 0 ? 'vi' : nav0.indexOf('en') === 0 ? 'en' : 'zh'))
})()
