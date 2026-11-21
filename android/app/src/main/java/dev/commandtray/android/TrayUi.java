package dev.commandtray.android;

import android.content.Context;
import android.content.res.ColorStateList;
import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.text.SpannableString;
import android.text.Spanned;
import android.text.TextUtils;
import android.text.style.DynamicDrawableSpan;
import android.text.style.ImageSpan;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import static dev.commandtray.android.R.color.*;

final class TrayUi {
    enum Icon { SPACE, FOLDER, CLIPBOARD, SETTINGS, PLUS, SYNC, DOWNLOAD, MORE, CHEVRON, NOTE, TASKS, BOARD, FILE, EDIT, COPY, SEND, SERVER, KEY }
    enum Style { PRIMARY, TONAL, TEXT }

    private final Context context;

    TrayUi(Context context) { this.context = context; }

    int color(int id) { return context.getColor(id); }
    int dp(int value) { return Math.round(value * context.getResources().getDisplayMetrics().density); }

    GradientDrawable fill(int color, int radius) {
        GradientDrawable shape = new GradientDrawable();
        shape.setColor(color(color));
        shape.setCornerRadius(dp(radius));
        return shape;
    }

    Drawable ripple(int color, int radius, int ink) {
        int tint = (color(ink) & 0x00FFFFFF) | 0x24000000;
        int focus = (color(ink) & 0x00FFFFFF) | 0x34000000;
        ColorStateList feedback = new ColorStateList(
            new int[][]{{android.R.attr.state_focused}, {}}, new int[]{focus, tint});
        return new RippleDrawable(feedback, color == 0 ? null : fill(color, radius), fill(tray_text, radius));
    }

    TextView text(CharSequence content, int size, int ink, boolean strong) {
        TextView view = new TextView(context);
        view.setText(content);
        view.setTextSize(size);
        view.setTextColor(color(ink));
        view.setIncludeFontPadding(false);
        if (strong) view.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        return view;
    }

    TextView heading(CharSequence content, int size, int ink) {
        TextView view = text(content, size, ink, true);
        view.setAccessibilityHeading(true);
        return view;
    }

    LinearLayout row() {
        LinearLayout view = new LinearLayout(context);
        view.setOrientation(LinearLayout.HORIZONTAL);
        view.setGravity(Gravity.CENTER_VERTICAL);
        view.setBaselineAligned(false);
        return view;
    }

    LinearLayout column() {
        LinearLayout view = new LinearLayout(context);
        view.setOrientation(LinearLayout.VERTICAL);
        return view;
    }

    LinearLayout surface(int background, int radius, int padding) {
        LinearLayout view = column();
        view.setBackground(fill(background, radius));
        int dimension = dp(padding);
        view.setPadding(dimension, dimension, dimension, dimension);
        return view;
    }

    void gap(LinearLayout parent, int size) {
        boolean vertical = parent.getOrientation() == LinearLayout.VERTICAL;
        parent.addView(new View(context), vertical
            ? new LinearLayout.LayoutParams(1, dp(size))
            : new LinearLayout.LayoutParams(dp(size), 1));
    }

    void stretch(LinearLayout row, View child) {
        row.addView(child, new LinearLayout.LayoutParams(0, -2, 1));
    }

    void rule(LinearLayout parent) {
        View line = new View(context);
        line.setBackgroundColor(color(tray_outline));
        parent.addView(line, new LinearLayout.LayoutParams(-1, dp(1)));
    }

    Button button(String content, Icon symbol, Style style, Runnable action) {
        int background = switch (style) {
            case PRIMARY -> tray_primary;
            case TONAL -> tray_tonal;
            case TEXT -> 0;
        };
        int ink = style == Style.PRIMARY ? tray_on_primary : tray_primary;
        Button view = new Button(context, null, 0);
        view.setText(content);
        view.setAllCaps(false);
        view.setTextSize(14);
        view.setTextColor(color(ink));
        view.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        view.setMinHeight(dp(48));
        view.setMinimumWidth(0);
        view.setGravity(Gravity.CENTER);
        view.setPadding(dp(12), dp(6), dp(12), dp(6));
        view.setBackground(ripple(background, 10, ink));
        view.setStateListAnimator(null);
        if (symbol != null) {
            Drawable art = icon(symbol, ink, 18);
            art.setBounds(0, 0, dp(18), dp(18));
            SpannableString caption = new SpannableString("\uFFFC\u00A0\u00A0" + content);
            caption.setSpan(new ImageSpan(art, DynamicDrawableSpan.ALIGN_CENTER), 0, 1, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE);
            view.setText(caption);
            view.setContentDescription(content);
        }
        view.setOnClickListener(child -> action.run());
        return view;
    }

    ImageButton iconButton(String description, Icon symbol, int ink, Runnable action) {
        return iconButton(description, symbol, ink, button -> action.run());
    }

    ImageButton iconButton(String description, Icon symbol, int ink, View.OnClickListener action) {
        ImageButton button = new ImageButton(context, null, 0);
        button.setImageDrawable(icon(symbol, ink, 20));
        button.setScaleType(ImageView.ScaleType.CENTER);
        button.setBackground(ripple(0, 20, ink));
        button.setContentDescription(description);
        button.setTooltipText(description);
        button.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        button.setOnClickListener(action);
        return button;
    }

    ImageView iconView(Icon symbol, int ink, int size) {
        ImageView icon = new ImageView(context);
        icon.setImageDrawable(icon(symbol, ink, size));
        icon.setScaleType(ImageView.ScaleType.CENTER);
        icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        icon.setLayoutParams(new LinearLayout.LayoutParams(dp(size), dp(size)));
        return icon;
    }

    EditText field(String hint, String value, int inputType) {
        EditText input = new EditText(context);
        input.setInputType(inputType);
        input.setId(View.generateViewId());
        input.setText(value);
        input.setHint(hint);
        input.setTextSize(15);
        input.setTextColor(color(tray_text));
        input.setHintTextColor(color(tray_muted));
        input.setMinHeight(dp(48));
        input.setPadding(dp(12), dp(10), dp(12), dp(10));
        GradientDrawable resting = fill(tray_canvas, 12);
        resting.setStroke(dp(1), color(tray_outline));
        GradientDrawable focused = fill(tray_canvas, 12);
        focused.setStroke(dp(2), color(tray_primary));
        android.graphics.drawable.StateListDrawable state = new android.graphics.drawable.StateListDrawable();
        state.addState(new int[]{android.R.attr.state_focused}, focused);
        state.addState(new int[0], resting);
        input.setBackground(state);
        return input;
    }

    void labeledField(LinearLayout parent, String label, EditText field) {
        TextView caption = text(label, 13, tray_muted, true);
        caption.setLabelFor(field.getId());
        parent.addView(caption);
        gap(parent, 6);
        parent.addView(field, new LinearLayout.LayoutParams(-1, -2));
    }

    final class Destination extends LinearLayout {
        private final Icon symbol;
        private final ImageView graphic;
        private final TextView label;

        Destination(String title, Icon symbol, boolean stacked, Runnable action) {
            super(context);
            this.symbol = symbol;
            setOrientation(stacked ? VERTICAL : HORIZONTAL);
            setGravity(Gravity.CENTER);
            setMinimumHeight(dp(stacked ? 52 : 48));
            setPadding(dp(2), dp(3), dp(2), dp(3));
            setContentDescription(title);
            setFocusable(true);
            setOnClickListener(view -> action.run());
            graphic = iconView(symbol, tray_muted, 18);
            addView(graphic);
            gap(this, stacked ? 3 : 5);
            label = text(title, 12, tray_muted, true);
            label.setMaxLines(1);
            label.setEllipsize(TextUtils.TruncateAt.END);
            label.setGravity(Gravity.CENTER);
            label.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            addView(label, new LayoutParams(-2, -2));
        }

        void showSelected(boolean active) {
            setSelected(active);
            setBackground(ripple(active ? tray_selected : 0, 12, tray_primary));
            int ink = active ? tray_primary : tray_muted;
            graphic.setImageDrawable(icon(symbol, ink, 18));
            label.setTextColor(color(ink));
        }
    }

    private Drawable icon(Icon symbol, int ink, int size) {
        return new Drawable() {
            private final Paint stroke = new Paint(Paint.ANTI_ALIAS_FLAG);
            {
                stroke.setColor(color(ink));
                stroke.setStyle(Paint.Style.STROKE);
                stroke.setStrokeWidth(1.75f);
                stroke.setStrokeCap(Paint.Cap.ROUND);
                stroke.setStrokeJoin(Paint.Join.ROUND);
            }

            @Override public int getIntrinsicWidth() { return dp(size); }
            @Override public int getIntrinsicHeight() { return dp(size); }
            @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
            @Override public void setAlpha(int alpha) { stroke.setAlpha(alpha); }
            @Override public void setColorFilter(ColorFilter filter) { stroke.setColorFilter(filter); }

            @Override public void draw(Canvas canvas) {
                canvas.save();
                canvas.translate(getBounds().left, getBounds().top);
                canvas.scale(getBounds().width() / 24f, getBounds().height() / 24f);
                switch (symbol) {
                    case SPACE -> { box(canvas, 3, 4, 21, 20); lines(canvas, 3, 9, 21, 9); lines(canvas, 10, 9, 10, 20); }
                    case FOLDER -> lines(canvas, 3, 6, 9, 6, 11, 8, 21, 8, 21, 19, 3, 19, 3, 6);
                    case CLIPBOARD -> { box(canvas, 5, 5, 19, 21); box(canvas, 9, 3, 15, 7); lines(canvas, 9, 12, 15, 12); lines(canvas, 9, 16, 14, 16); }
                    case SETTINGS -> {
                        lines(canvas, 10, 3, 14, 3, 15, 6, 17, 7, 20, 7, 22, 11, 20, 13, 20, 15, 21, 17, 18, 20,
                            15, 19, 13, 21, 10, 21, 9, 18, 7, 17, 4, 17, 2, 13, 4, 11, 4, 9, 3, 7, 6, 4, 9, 5, 10, 3);
                        canvas.drawCircle(12, 12, 3, stroke);
                    }
                    case PLUS -> { lines(canvas, 12, 5, 12, 19); lines(canvas, 5, 12, 19, 12); }
                    case SYNC -> {
                        canvas.drawArc(4, 4, 20, 20, 198, 148, false, stroke);
                        canvas.drawArc(4, 4, 20, 20, 18, 148, false, stroke);
                        lines(canvas, 20, 5, 20, 10, 15, 10);
                        lines(canvas, 4, 19, 4, 14, 9, 14);
                    }
                    case DOWNLOAD -> { lines(canvas, 12, 3, 12, 15); lines(canvas, 7, 10, 12, 15, 17, 10); lines(canvas, 4, 16, 4, 20, 20, 20, 20, 16); }
                    case MORE -> { canvas.drawCircle(12, 5, 0.8f, stroke); canvas.drawCircle(12, 12, 0.8f, stroke); canvas.drawCircle(12, 19, 0.8f, stroke); }
                    case CHEVRON -> lines(canvas, 6, 9, 12, 15, 18, 9);
                    case NOTE -> { lines(canvas, 4, 4, 20, 4, 20, 15, 15, 20, 4, 20, 4, 4); lines(canvas, 15, 20, 15, 15, 20, 15); lines(canvas, 8, 9, 16, 9); }
                    case TASKS -> { lines(canvas, 3, 7, 5, 9, 9, 5); lines(canvas, 12, 7, 21, 7); lines(canvas, 3, 16, 5, 18, 9, 14); lines(canvas, 12, 16, 21, 16); }
                    case BOARD -> { box(canvas, 3, 4, 21, 20); lines(canvas, 6, 16, 11, 9, 14, 14, 18, 10); }
                    case FILE -> { lines(canvas, 6, 3, 14, 3, 19, 8, 19, 21, 6, 21, 6, 3); lines(canvas, 14, 3, 14, 8, 19, 8); lines(canvas, 9, 13, 16, 13); lines(canvas, 9, 17, 16, 17); }
                    case EDIT -> { lines(canvas, 4, 20, 5, 15, 16, 4, 20, 8, 9, 19, 4, 20); lines(canvas, 14, 6, 18, 10); }
                    case COPY -> { box(canvas, 8, 8, 20, 20); lines(canvas, 15, 4, 4, 4, 4, 15); }
                    case SEND -> { lines(canvas, 3, 10, 21, 3, 15, 21, 11, 13, 3, 10); lines(canvas, 11, 13, 21, 3); }
                    case SERVER -> { box(canvas, 3, 4, 21, 11); box(canvas, 3, 13, 21, 20); lines(canvas, 7, 7.5f, 8, 7.5f); lines(canvas, 7, 16.5f, 8, 16.5f); }
                    case KEY -> { canvas.drawCircle(8, 8, 4.5f, stroke); lines(canvas, 11.5f, 11.5f, 20, 20, 20, 16, 17, 16); }
                }
                canvas.restore();
            }

            private void box(Canvas canvas, float left, float top, float right, float bottom) {
                canvas.drawRoundRect(left, top, right, bottom, 2, 2, stroke);
            }

            private void lines(Canvas canvas, float... coordinates) {
                Path path = new Path();
                path.moveTo(coordinates[0], coordinates[1]);
                for (int i = 2; i < coordinates.length; i += 2) path.lineTo(coordinates[i], coordinates[i + 1]);
                canvas.drawPath(path, stroke);
            }
        };
    }
}
