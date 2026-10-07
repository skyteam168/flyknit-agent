"""启动时检查 .env 里读不懂的行。

python-dotenv 碰到这种行只 warning 一句行号就跳过，那一项悄悄退回默认值。
线上真实发生过：改了 ENROLLMENT_KEY，重启，日志里六条 "could not parse statement"
混在 pydantic 的 UserWarning 里，没人注意；密钥其实还是代码里的默认值。

「以为改了其实没改」比「没改」更危险，所以这些行要按 ERROR 单独列出来。
"""

from app.main import unparsable_env_lines


def write(tmp_path, text):
    p = tmp_path / ".env"
    p.write_text(text, encoding="utf-8")
    return p


def test_a_line_dotenv_cannot_parse_is_reported(tmp_path):
    bad = unparsable_env_lines(write(tmp_path, "FLYKNIT_ADMIN_TOKEN=ok\nsome broken line\n"))
    assert [b.number for b in bad] == [2]
    assert "读不懂" in bad[0].hint


def test_a_comment_missing_its_hash_is_reported(tmp_path):
    """没有等号的行 dotenv 不报错，直接当成一个空设置吃掉——多半是忘了写 # 的注释。"""
    bad = unparsable_env_lines(write(tmp_path, "FLYKNIT_ADMIN_TOKEN=ok\n改成新密钥了\n"))
    assert [b.number for b in bad] == [2]
    assert "没有等号" in bad[0].hint


def test_the_value_never_reaches_the_log(tmp_path):
    """等号后面可能就是密钥。提示里只回显等号前面那段。"""
    bad = unparsable_env_lines(write(tmp_path, 'FLYKNIT_X="未闭合的引号\nFLYKNIT_ADMIN_TOKEN=ok\n'))
    assert bad and all("未闭合的引号" not in b.hint for b in bad)


def test_normal_lines_are_quiet(tmp_path):
    text = (
        "# 这是注释\n"
        "\n"
        "FLYKNIT_ADMIN_TOKEN=abc123\n"
        'FLYKNIT_SECRET_KEY="带 空格 的值"\n'
        "FLYKNIT_DATABASE_URL=sqlite+aiosqlite:///./flyknit.db\n"
    )
    assert unparsable_env_lines(write(tmp_path, text)) == []


def test_a_missing_file_is_not_an_error(tmp_path):
    assert unparsable_env_lines(tmp_path / "nope.env") == []
